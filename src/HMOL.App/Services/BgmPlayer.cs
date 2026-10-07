using System.IO;
using HMOL.Core.App;
using HMOL.Core.Logging;
using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using HMOL.Core.Localization;

namespace HMOL.App.Services;

/// <summary>
/// 主页背景音乐。解码走 Media Foundation（NAudio 的 <see cref="MediaFoundationReader"/>），输出走 WASAPI 共享模式、
/// 失败再退到 waveOut。设置里的 <see cref="BgmSettings"/> 就是唯一状态源：播放列表、音量、随机、循环、当前曲目都存这里，
/// 所以界面改音量、换曲目都能立刻落盘、下次启动接着放。对应旧版 HMOL_qt.py 的 BgmManager（第 2329-2477 行）。
///
/// 为什么不用 WPF 自带的 MediaPlayer：它依赖 Windows Media Player（wmp.dll）。精简版 Windows
/// （LTSC / N 版）不带 WMP，MediaPlayer 即使没载入任何媒体也会异步抛 MediaFailed
/// （"Windows Media Player version 10 or later is required."），用户开了开关也放不出声。
/// Media Foundation 在这类系统上齐全，MP3 / WAV / FLAC / M4A / AAC / WMA 都能解。
///
/// 两个与 MediaPlayer 版不同的实现细节：
/// 1) 一曲结束用输出设备的 PlaybackStopped 事件判定，不再靠定时器比对播放位置与时长；
/// 2) 解码与输出都在"载入当前曲目"时按需创建，未启用时一个文件都不打开。
/// 另外保留一个兜底：光靠 PlaybackStopped，遇到既不吐数据也不报结束的解码器 / 输出设备会永久僵在那里，
/// 所以再加一个"播放停滞看门狗"（<see cref="CheckStall"/>），位置长时间不推进就按失败路径跳过当前曲目。
/// </summary>
internal static class BgmPlayer
{
    /// <summary>能播的音频扩展名。实际能不能解由 Media Foundation 现场决定，认不出的会在载入时跳过。</summary>
    private static readonly string[] AudioExtensions = [".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac", ".ogg"];

    /// <summary>输出缓冲时长（毫秒）。太小容易断音，太大切歌会有拖尾。</summary>
    private const int OutputLatencyMs = 200;

    /// <summary>看门狗巡检间隔（毫秒）：判定精度与开销的折中，最坏多等一轮才判出停滞。</summary>
    private const int WatchdogIntervalMs = 500;

    /// <summary>
    /// 停滞判定阈值：播放位置连续这么久没推进就认定僵住。
    /// 取值依据：旧版实现（HMOL_qt.py 的 BgmManager）的兜底就是"位置 2 秒不推进就跳下一首"，
    /// 这里沿用同一个 2 秒。
    /// </summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 起播宽限期：刚载入曲目时解码器与输出设备可能还没开始拉数据，这段时间内位置不动是正常的，不判定停滞。
    /// 取 5 秒（2.5 倍阈值）：正常起播到第一次推进通常在 1 秒内，留足慢解码 / 慢设备余量，免得误杀正常曲目。
    /// </summary>
    private static readonly TimeSpan StallGrace = TimeSpan.FromSeconds(5);

    private static bool _initialized;
    private static bool _playing;
    private static int _failedStreak;

    /// <summary>看门狗状态（位置基准、变化时刻、宽限截止）的锁：巡检线程与切歌 / 暂停的线程会同时读写。</summary>
    private static readonly object WatchdogLock = new();

    private static System.Threading.Timer? _watchdog;

    /// <summary>巡检重入保护：1 表示上一轮还在处理（正在跳曲），本轮直接放弃。</summary>
    private static int _watchdogBusy;

    private static long _lastPosition;
    private static DateTime _lastPositionChangeAt;
    private static DateTime _graceUntil;

    /// <summary>曲目代号。每次载入 / 释放都自增，用来丢弃切换过程中迟到的 PlaybackStopped。</summary>
    private static int _generation;

    private static string? _currentPath;
    private static MediaFoundationReader? _reader;
    private static VolumeSampleProvider? _volumeProvider;
    private static IWavePlayer? _output;
    private static EventHandler<StoppedEventArgs>? _stopHandler;

    /// <summary>播放状态变化（换曲、暂停、停止、失败跳过）。订阅方负责刷新界面。</summary>
    public static event Action? StateChanged;

    /// <summary>可添加的音频扩展名，给文件选择框构造过滤器。</summary>
    public static IReadOnlyList<string> SupportedExtensions => AudioExtensions;

    /// <summary>文件选择框的通配串，形如 <c>*.mp3;*.wav;…</c>。</summary>
    public static string PickPattern => string.Join(";", AudioExtensions.Select(ext => "*" + ext));

    /// <summary>是否正在出声（已载入曲目且处于播放状态）。</summary>
    public static bool IsPlaying => _playing;

    /// <summary>当前曲目的文件名；没有当前曲目时为空串。</summary>
    public static string CurrentTrackName
    {
        get
        {
            var list = Settings.Playlist;
            var index = Settings.CurrentIndex;
            return index >= 0 && index < list.Count ? Path.GetFileName(list[index]) : string.Empty;
        }
    }

    private static BgmSettings Settings => SettingsStore.Current.Bgm;

    /// <summary>设置里的 0-100 音量换算成 NAudio 要的 0.0-1.0。</summary>
    private static float Volume01 => (float)(Math.Clamp(Settings.Volume, 0, 100) / 100d);

    /// <summary>能不能当曲目：扩展名在白名单里且文件存在。</summary>
    public static bool IsAudioFile(string? path)
        => !string.IsNullOrWhiteSpace(path) &&
           AudioExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // ————— 生命周期 —————

    /// <summary>
    /// 程序启动时调用一次：起 Media Foundation，按设置恢复音量，开启时自动播放。
    /// 未启用时这里不碰任何音频文件，也不建输出设备。
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try { MediaFoundationApi.Startup(); }
        catch (Exception ex) { Log.Debug(Loc.F("背景音乐：Media Foundation 初始化失败：{0}", ex.Message)); }

        ApplyVolume();

        if (Settings.Enabled) Play();
        else Log.Info(Loc.T("背景音乐：设置里未启用，本次启动不播放"));
    }

    /// <summary>退出时调用：停掉播放并释放文件句柄。</summary>
    public static void Shutdown() => StopPlayback();

    // ————— 播放控制 —————

    /// <summary>播放当前曲目；没有当前曲目时从第一首开始。</summary>
    public static void Play()
    {
        if (!_initialized)
        {
            Initialize();
            return;
        }

        if (!Settings.Enabled)
        {
            Log.Debug(Loc.T("背景音乐：未启用，忽略播放请求"));
            return;
        }

        if (Settings.Playlist.Count == 0)
        {
            Log.Info(Loc.T("背景音乐：播放列表为空，未播放"));
            return;
        }

        ClampIndex();
        LoadCurrent();
    }

    public static void Pause()
    {
        _playing = false;
        StopWatchdog();

        try { _output?.Pause(); }
        catch (Exception ex) { Log.Warn(Loc.F("背景音乐暂停失败：{0}", ex.Message)); }

        NotifyStateChanged();
    }

    public static void TogglePlayPause()
    {
        if (_playing)
        {
            Pause();
            return;
        }

        // 暂停后继续：解码器与输出都还开着，直接接着放，不重新解码
        if (_output is not null && _reader is not null)
        {
            _playing = true;

            try { _output.Play(); }
            catch (Exception ex) { Log.Warn(Loc.F("背景音乐继续播放失败：{0}", ex.Message)); }

            StartWatchdog();
            NotifyStateChanged();
            return;
        }

        Play();
    }

    /// <summary>切下一首。手动切换一定会换曲（列表非空时）。</summary>
    public static void Next()
    {
        if (Settings.Playlist.Count == 0) return;

        _failedStreak = 0;
        if (!MoveNext(auto: false)) return;

        LoadCurrent();
    }

    /// <summary>切上一首。第一首再往前就跳到最后一首（与旧版 _play_prev 一致）。</summary>
    public static void Previous()
    {
        var list = Settings.Playlist;
        if (list.Count == 0) return;

        _failedStreak = 0;

        var index = Settings.CurrentIndex;
        index = index <= 0 ? list.Count - 1 : index - 1;

        SetIndex(index);
        LoadCurrent();
    }

    public static void StopPlayback()
    {
        _playing = false;
        ReleasePlayback();
        NotifyStateChanged();
    }

    /// <summary>按设置里的开关启停：开启且有列表时开始播放，关闭时停止。</summary>
    public static void ApplyEnabled()
    {
        if (!Settings.Enabled)
        {
            StopPlayback();
            return;
        }

        // 已经在放就别重头来
        if (_playing) return;

        Play();
    }

    /// <summary>把设置里的 0-100 音量应用到当前输出（NAudio 的音量是 0.0-1.0）。</summary>
    public static void ApplyVolume()
    {
        // 还没载入曲目：载入时会按设置里的新值建音量节点，这里不用管
        if (_volumeProvider is null) return;

        _volumeProvider.Volume = Volume01;
    }

    // ————— 播放列表 —————

    /// <summary>添加曲目。返回（新增数，跳过数）：已存在、格式不支持或文件不存在的会被跳过。</summary>
    public static (int Added, int Skipped) AddTracks(IEnumerable<string> paths)
    {
        var list = Settings.Playlist;
        var added = 0;
        var skipped = 0;

        foreach (var path in paths)
        {
            var valid = !string.IsNullOrWhiteSpace(path)
                        && File.Exists(path)
                        && IsAudioFile(path)
                        && !list.Any(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));

            if (!valid)
            {
                skipped++;
                continue;
            }

            list.Add(path);
            added++;
        }

        if (added > 0)
        {
            if (list.Count == added) Settings.CurrentIndex = 0;

            SettingsStore.Save();
            Log.Info(Loc.F("背景音乐：新增 {0} 首曲目，当前共 {1} 首", added, list.Count));
        }

        NotifyStateChanged();
        return (added, skipped);
    }

    /// <summary>移除指定下标的曲目；移除的正是当前曲目时按需要换曲或停止。</summary>
    public static void RemoveTrack(int index)
    {
        var list = Settings.Playlist;
        if (index < 0 || index >= list.Count) return;

        var wasCurrent = index == Settings.CurrentIndex;
        list.RemoveAt(index);

        if (list.Count == 0)
        {
            Settings.CurrentIndex = 0;
            StopPlayback();
        }
        else if (wasCurrent)
        {
            Settings.CurrentIndex = Math.Min(index, list.Count - 1);
            if (_playing) LoadCurrent();
        }
        else if (index < Settings.CurrentIndex)
        {
            Settings.CurrentIndex--;
        }

        SettingsStore.Save();
        Log.Info(Loc.F("背景音乐：已移除曲目，剩余 {0} 首", list.Count));
        NotifyStateChanged();
    }

    // ————— 内部 —————

    /// <summary>
    /// 载入当前曲目。这是全类唯一打开音频文件的地方：关闭状态、列表为空一律收手，
    /// 连输出设备都不建，保证「未启用时没有任何播放动作」。
    /// </summary>
    private static void LoadCurrent()
    {
        var list = Settings.Playlist;

        if (list.Count == 0 || !Settings.Enabled)
        {
            Log.Debug(list.Count == 0 ? Loc.T("背景音乐：播放列表为空，不播放") : Loc.T("背景音乐：未启用，不播放"));
            StopPlayback();
            return;
        }

        ClampIndex();
        var path = list[Settings.CurrentIndex];

        if (!File.Exists(path) || !IsAudioFile(path))
        {
            Log.Warn(Loc.F("背景音乐：{0} 已不存在或不是支持的音频格式，已跳过", Path.GetFileName(path)));
            AdvanceOnFailure();
            return;
        }

        // 换曲前先放掉旧曲：既立刻收声，也把上一首的文件句柄放掉，用户才删得掉它
        ReleasePlayback();
        var generation = _generation;

        MediaFoundationReader reader;
        VolumeSampleProvider volume;

        try
        {
            reader = new MediaFoundationReader(path);
            volume = new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = Volume01 };
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("背景音乐：{0} 解不开（{1}），已跳过", Path.GetFileName(path), DescribeDecodeError(ex)));
            AdvanceOnFailure();
            return;
        }

        IWavePlayer output;

        try
        {
            output = OpenOutput(volume);
        }
        catch (Exception ex)
        {
            reader.Dispose();
            Log.Warn(Loc.F("背景音乐：音频输出设备不可用（{0}），本次不播放", ex.Message));
            StopPlayback();
            return;
        }

        // 先把状态摆好再出声：极短的曲目可能在 Play 之后立刻触发 PlaybackStopped
        var handler = new EventHandler<StoppedEventArgs>((_, e) => OnPlaybackStopped(generation, e));

        _reader = reader;
        _volumeProvider = volume;
        _output = output;
        _stopHandler = handler;
        _currentPath = path;
        _playing = true;

        output.PlaybackStopped += handler;

        try
        {
            output.Play();
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("背景音乐播放失败：{0}", ex.Message));
            StopPlayback();
            return;
        }

        // 失败计数不在这里清零：解码成功不等于放得出声（可能一载入就僵住），
        // 清零交给看门狗——只有位置真的在推进才算这一轮播放活着，否则会无限跳过。
        StartWatchdog();

        Log.Info(Loc.F("背景音乐：载入 {0}（{1}/{2}）", Path.GetFileName(path), Settings.CurrentIndex + 1, list.Count));
        NotifyStateChanged();
    }

    /// <summary>建输出设备并接上解码流：优先 WASAPI 共享模式，端点不可用时退回 waveOut。</summary>
    private static IWavePlayer OpenOutput(ISampleProvider source)
    {
        IWavePlayer? wasapi = null;

        try
        {
            wasapi = new WasapiOut(AudioClientShareMode.Shared, OutputLatencyMs);
            wasapi.Init(source);
            return wasapi;
        }
        catch (Exception ex)
        {
            Log.Debug(Loc.F("背景音乐：WASAPI 输出不可用，改用 waveOut：{0}", ex.Message));
            try { wasapi?.Dispose(); } catch { /* 打不开的设备，释放失败无所谓 */ }
        }

        var waveOut = new WaveOutEvent { DesiredLatency = OutputLatencyMs };
        waveOut.Init(source);
        return waveOut;
    }

    /// <summary>一曲放完（或解码中途出错）时被输出设备通知。可能在任意线程上，丢给线程池处理。</summary>
    private static void OnPlaybackStopped(int generation, StoppedEventArgs e)
    {
        // 切歌 / 停止时我们自己关掉的输出也可能报到，认代号丢弃迟到的通知
        if (generation != _generation) return;

        var error = e.Exception;
        ThreadPool.QueueUserWorkItem(_ => HandleStopped(generation, error));
    }

    private static void HandleStopped(int generation, Exception? error)
    {
        if (generation != _generation) return;

        if (error is not null)
        {
            Log.Warn(Loc.F("背景音乐：{0} 播放中断", Path.GetFileName(_currentPath ?? string.Empty)) +
                     Loc.F("（{0}），尝试跳到下一首", DescribeDecodeError(error)));

            AbandonCurrent();
            return;
        }

        Log.Info(Loc.F("背景音乐：{0} 播放完毕", CurrentTrackName));
        _failedStreak = 0;

        if (!MoveNext(auto: true)) return;

        LoadCurrent();
    }

    /// <summary>关掉输出、放掉解码器（连同文件句柄），并作废在途的 PlaybackStopped。</summary>
    private static void ReleasePlayback()
    {
        // 停止、切歌、失败跳过、退出都走这里，计时器一并收掉，不留空转的看门狗
        StopWatchdog();

        _generation++;
        _currentPath = null;

        var output = _output;
        var handler = _stopHandler;

        _output = null;
        _stopHandler = null;
        _volumeProvider = null;

        if (output is not null)
        {
            try
            {
                // 先退订再关：Dispose 触发的停止事件不该被当成「一曲放完」
                if (handler is not null) output.PlaybackStopped -= handler;

                output.Stop();
                output.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn(Loc.F("背景音乐停止失败：{0}", ex.Message));
            }
        }

        try { _reader?.Dispose(); }
        catch (Exception ex) { Log.Debug(Loc.F("背景音乐：释放解码器时出错：{0}", ex.Message)); }

        _reader = null;
    }

    /// <summary>
    /// 当前曲目放不下去（输出报错或播放停滞）：收声清状态，再走既有的失败跳过路径。
    /// 与"解码失败"共用同一套 AdvanceOnFailure 保护，不另造一套跳过机制。
    /// </summary>
    private static void AbandonCurrent()
    {
        _playing = false;
        ReleasePlayback();
        AdvanceOnFailure();
    }

    // ————— 播放停滞看门狗 —————

    /// <summary>开始巡检当前曲目：重置位置基准与宽限期，然后按固定间隔看位置有没有推进。</summary>
    private static void StartWatchdog()
    {
        lock (WatchdogLock)
        {
            StopWatchdog();

            var now = DateTime.UtcNow;

            _lastPosition = ReadPlaybackPosition() ?? 0;
            _lastPositionChangeAt = now;
            _graceUntil = now + StallGrace;
            _watchdog = new System.Threading.Timer(OnWatchdogTick, null, WatchdogIntervalMs, WatchdogIntervalMs);
        }
    }

    /// <summary>停掉巡检并释放计时器。暂停、停止、切歌、退出都会走到这里，保证不留空转的计时器。</summary>
    private static void StopWatchdog()
    {
        lock (WatchdogLock)
        {
            var timer = _watchdog;
            _watchdog = null;
            timer?.Dispose();
        }
    }

    /// <summary>
    /// 判定是否「播放停滞」的纯函数：不碰计时器、不碰音频设备，只按输入算，边界条件可直接断言。
    /// 三个条件同时成立才判停滞：处于播放中（暂停 / 已停止传 false）、已过起播宽限期、位置在阈值内一次没变。
    /// </summary>
    /// <param name="playing">是否处于播放状态。</param>
    /// <param name="now">当前时刻。</param>
    /// <param name="position">本次采样到的播放位置。</param>
    /// <param name="lastPosition">上次观察到位置变化时的位置。</param>
    /// <param name="lastChangeAt">位置最近一次发生变化的时刻。</param>
    /// <param name="graceUntil">起播宽限截止时刻，<paramref name="now"/> 未到就不判定。</param>
    /// <param name="timeout">停滞阈值，位置静止满这么久即判定。</param>
    internal static bool IsStalled(bool playing, DateTime now, long position, long lastPosition,
        DateTime lastChangeAt, DateTime graceUntil, TimeSpan timeout)
    {
        if (!playing) return false;                  // 暂停 / 停止：绝不判定
        if (now < graceUntil) return false;          // 宽限期内：还没开始推进属正常
        if (position != lastPosition) return false;  // 位置又在动：输出设备还在消费数据
        return now - lastChangeAt >= timeout;        // 静止满阈值：判定僵住
    }

    /// <summary>看门狗巡检线程入口。可能在任意线程上，重入时直接放弃本轮。</summary>
    private static void OnWatchdogTick(object? state)
    {
        // 上一轮还没收尾（正在跳曲）就别插队，避免同一首被连判两次
        if (Interlocked.Exchange(ref _watchdogBusy, 1) == 1) return;

        try { CheckStall(); }
        catch (Exception ex) { Log.Warn(Loc.F("背景音乐：播放停滞检查出错：{0}", ex.Message)); }
        finally { Interlocked.Exchange(ref _watchdogBusy, 0); }
    }

    /// <summary>
    /// 巡检一次：位置在 <see cref="StallTimeout"/> 内没推进就当这一首放不下去，记一条告警后走失败跳过路径。
    /// 计时器只负责调用这里，判定本身在 <see cref="IsStalled"/> 里。
    /// </summary>
    private static void CheckStall()
    {
        int generation;
        string? stalledTrack = null;

        lock (WatchdogLock)
        {
            // 已被停掉（暂停 / 停止 / 退出）：迟到的回调什么都不做
            if (_watchdog is null) return;

            var now = DateTime.UtcNow;
            var position = ReadPlaybackPosition();

            if (position is null)
            {
                // 读不到位置说明输出 / 解码器正在收摊，这一轮不算数，把时间挪到当下免得恢复后立刻被判停滞
                _lastPositionChangeAt = now;
                return;
            }

            if (!IsStalled(_playing, now, position.Value, _lastPosition, _lastPositionChangeAt, _graceUntil, StallTimeout))
            {
                if (position.Value != _lastPosition)
                {
                    _lastPosition = position.Value;
                    _lastPositionChangeAt = now;

                    // 位置真的在推进，这一轮播放活着：清掉失败计数（载入成功时不再清零）
                    _failedStreak = 0;
                }

                return;
            }

            generation = _generation;
            stalledTrack = CurrentTrackName;
        }

        // 判定与处理之间用户可能已经手动切过曲，这次停滞作废
        if (generation != _generation) return;

        Log.Warn(Loc.F("背景音乐：{0} 播放停滞（位置 {1:0.#} 秒未推进，", stalledTrack, StallTimeout.TotalSeconds) +
                 Loc.F("输出{0}），已跳过", OutputStateText()));

        AbandonCurrent();
    }

    /// <summary>
    /// 采样播放位置（字节）：输出设备已播字节与解码器已读字节取较大者——两者都只随设备消费数据前进，
    /// 取 max 保证任一个在动就算有进展（应对设备报不出位置的情况）。都取不到时返回 null。
    /// </summary>
    private static long? ReadPlaybackPosition()
    {
        long? device = null;
        if (_output is IWavePosition position)
        {
            try { device = position.GetPosition(); }
            catch { device = null; }
        }

        long? stream = null;
        try { stream = _reader?.Position; }
        catch { stream = null; }

        return (device, stream) switch
        {
            (not null, not null) => Math.Max(device.Value, stream.Value),
            (not null, null) => device,
            (null, not null) => stream,
            _ => null
        };
    }

    /// <summary>
    /// 输出设备自报的状态，只写进日志供排查。它不能用作判定条件：设备僵住时它往往还是 Playing，
    /// 而真正出错时 PlaybackStopped 事件已经走到 <see cref="HandleStopped"/> 了。
    /// </summary>
    private static string OutputStateText()
    {
        try { return _output?.PlaybackState.ToString() ?? Loc.T("无输出设备"); }
        catch { return Loc.T("状态未知"); }
    }

    /// <summary>算下一首的下标。返回 false 表示列表已放完且未开循环，调用方不要再播。</summary>
    private static bool MoveNext(bool auto)
    {
        var list = Settings.Playlist;
        if (list.Count == 0)
        {
            StopPlayback();
            return false;
        }

        var index = Settings.CurrentIndex;

        if (Settings.Shuffle)
        {
            SetIndex(PickRandom(list.Count, index));
            return true;
        }

        if (index + 1 < list.Count)
        {
            SetIndex(index + 1);
            return true;
        }

        // 循环开启，或用户手动点「下一首」：回到第一首
        if (Settings.Loop || !auto)
        {
            SetIndex(0);
            return true;
        }

        Log.Info(Loc.T("背景音乐：列表已放完且未开启循环，停止播放"));
        StopPlayback();
        return false;
    }

    /// <summary>连续播不了就往下跳；整列表都播不了时停止，避免死循环。</summary>
    private static void AdvanceOnFailure()
    {
        _failedStreak++;

        if (_failedStreak >= Math.Max(1, Settings.Playlist.Count))
        {
            Log.Warn(Loc.T("背景音乐：列表里的曲目都没能播放，已停止"));
            StopPlayback();
            return;
        }

        if (!MoveNext(auto: true)) return;

        LoadCurrent();
    }

    /// <summary>随机下一首：尽量不重复当前这首（只有一首时无从避免）。</summary>
    private static int PickRandom(int count, int current)
    {
        if (count <= 1) return 0;

        var index = Random.Shared.Next(count - 1);
        return index >= current ? index + 1 : index;
    }

    /// <summary>把解码 / 播放异常翻成用户看得懂的话。0xC00Dxxxx 段是 Media Foundation 自己的错误。</summary>
    private static string DescribeDecodeError(Exception ex)
    {
        return (ex.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0xC00D0000)
            ? Loc.F("系统缺少该格式的解码器，或文件已损坏（0x{0:X8}）", ex.HResult)
            : ex.Message;
    }

    private static void SetIndex(int index)
    {
        Settings.CurrentIndex = index;
        SettingsStore.Save();
    }

    /// <summary>下标可能因为手工改过配置文件而越界，用之前先夹回列表范围。</summary>
    private static void ClampIndex()
    {
        var count = Settings.Playlist.Count;

        Settings.CurrentIndex = count == 0 ? 0 : Math.Clamp(Settings.CurrentIndex, 0, count - 1);
    }

    /// <summary>
    /// 通知界面刷新。退出流程里界面可能已经在收摊，刷新失败不该把退出也带崩，所以这里吞掉异常。
    /// 可能来自播放线程，订阅方自己负责切回 UI 线程。
    /// </summary>
    private static void NotifyStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("背景音乐状态刷新失败：{0}", ex.Message));
        }
    }
}
