namespace HMOL.Core.Multiplayer;

/// <summary>
/// 收藏队友。对应旧版 friends.py：数据随联机配置保存在 Data\multiplayer.json 的 Friends 字段，
/// 纯本地不上传。同 IP 或同昵称视为同一个队友。
/// </summary>
public static class FriendStore
{
    /// <summary>收藏列表，按最近一起联机的时间倒序。对应 friends.py:25 <c>list_friends</c>。</summary>
    public static IReadOnlyList<FriendEntry> List()
        => MultiplayerSettingsStore.Current.Friends
            .OrderByDescending(friend => friend.LastSeen)
            .ToArray();

    /// <summary>新增/更新收藏：记录一起联机的次数与最近时间。对应 friends.py:32 <c>upsert</c>。</summary>
    public static void Upsert(string? name, string? ip, string? community = null)
    {
        var safeName = ChatCrypt.SanitizeText(name, 32);
        var safeIp = (ip ?? string.Empty).Trim();

        if (safeName.Length == 0 || safeIp.Length == 0) return;

        MultiplayerSettingsStore.Update(settings =>
        {
            var friend = settings.Friends.FirstOrDefault(item => item.Ip == safeIp || item.Name == safeName);

            if (friend is null)
            {
                settings.Friends.Add(new FriendEntry
                {
                    Name = safeName,
                    Ip = safeIp,
                    Community = community ?? string.Empty,
                    Times = 1,
                    LastSeen = DateTime.Now
                });

                return;
            }

            friend.Name = safeName;
            friend.Ip = safeIp;
            if (!string.IsNullOrEmpty(community)) friend.Community = community;
            friend.Times++;
            friend.LastSeen = DateTime.Now;
        });
    }

    /// <summary>按 IP 或昵称删除收藏。对应 friends.py:55 <c>remove</c>。</summary>
    public static void Remove(string? ip = null, string? name = null)
    {
        var safeIp = (ip ?? string.Empty).Trim();
        var safeName = (name ?? string.Empty).Trim();

        if (safeIp.Length == 0 && safeName.Length == 0) return;

        MultiplayerSettingsStore.Update(settings => settings.Friends.RemoveAll(friend =>
            (safeIp.Length > 0 && friend.Ip == safeIp) || (safeName.Length > 0 && friend.Name == safeName)));
    }

    /// <summary>仅更新最近时间与次数（联机成功时调用）。对应 friends.py:69 <c>touch</c>。</summary>
    public static void Touch(string? ip, string? community = null)
    {
        var safeIp = (ip ?? string.Empty).Trim();
        if (safeIp.Length == 0) return;

        MultiplayerSettingsStore.Update(settings =>
        {
            foreach (var friend in settings.Friends.Where(item => item.Ip == safeIp))
            {
                friend.LastSeen = DateTime.Now;
                friend.Times++;
                if (!string.IsNullOrEmpty(community)) friend.Community = community;
            }
        });
    }
}
