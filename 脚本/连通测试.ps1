Write-Host ""
Write-Host "=== MO LAN direct-connect test listener ==="
Write-Host "Listening on 127.0.0.1:1233 ..."
Write-Host "Now go to the MO client -> LAN lobby -> type  127.0.0.1:1233  and press Enter."
Write-Host ""

try {
    $l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Parse('127.0.0.1'), 1233)
    $l.Start()
} catch {
    Write-Host "[!] Cannot listen on port 1233 : $($_.Exception.Message)"
    Write-Host "    (maybe the game/client is already using it - close them and retry)"
    Read-Host "Press Enter to exit"
    exit
}

try {
    $t = $l.AcceptTcpClient()
    Write-Host ""
    Write-Host "*** CONNECTED! from $($t.Client.RemoteEndPoint)"
    Write-Host "*** That means the direct-connect really works."
    Write-Host ""
    try {
        $s = $t.GetStream()
        $s.ReadTimeout = 5000
        $buf = New-Object byte[] 256
        $n = $s.Read($buf, 0, 256)
        $txt = [Text.Encoding]::UTF8.GetString($buf, 0, $n)
        $txt = $txt.Replace([char]1, '|').Replace([char]2, '#')
        Write-Host "*** handshake received ($n bytes): $txt"
        if ($txt.StartsWith('JOIN')) { Write-Host "*** JOIN handshake is correct." }
    } catch {
        Write-Host "(no handshake data read, but the TCP connection itself succeeded)"
    }
    $t.Close()
} catch {
    Write-Host "[!] error: $($_.Exception.Message)"
}

$l.Stop()
Write-Host ""
Read-Host "Press Enter to exit"
