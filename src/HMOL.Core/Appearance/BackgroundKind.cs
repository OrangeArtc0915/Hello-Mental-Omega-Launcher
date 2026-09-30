namespace HMOL.Core.Appearance;

/// <summary>
/// 背景素材类型。类型不在设置里持久化，每次按文件扩展名重新判定
/// （判定入口 <see cref="BackgroundService.DetectKind"/>），设错了也不会残留旧类型。
/// </summary>
public enum BackgroundKind
{
    /// <summary>不使用背景，露出主题渐变。</summary>
    None = 0,

    /// <summary>静态图片。</summary>
    Image = 1,

    /// <summary>GIF 动图，由 App 层逐帧播放。</summary>
    Gif = 2,

    /// <summary>视频，静音循环播放。</summary>
    Video = 3
}
