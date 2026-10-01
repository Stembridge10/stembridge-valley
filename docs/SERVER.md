# Running the server (owner)

Download **StembridgeValley-Server.zip** from the latest release and unzip it anywhere (e.g. Documents).

## First time only

1. Double-click **Allow Through Firewall.cmd** and say Yes to the admin prompt.
2. Router: forward **UDP port 24642** to this PC. The server tries to do this automatically;
   **Server Status** tells you if it worked. If not, do it once in your router app
   (TP-Link Deco: More → Advanced → NAT Forwarding → Port Forwarding → add UDP 24642 to this PC,
   and reserve this PC's address under Address Reservation so it doesn't change).

## Every day

- **Start Server.cmd** — starts the hidden server. Nothing appears on screen; it runs at low priority so your own games aren't slowed down.
- **Server Status.cmd** — shows if it's running, the in-game date, who's online, and the invite code to send friends.
- **Stop Server.cmd** — stops it.

The farm and saves live in `%LOCALAPPDATA%\StembridgeValley-Server`. SMAPI's SaveBackup keeps the last 10 save backups there too.

## Invite code

`sv:<your public IP>:24642/<password>` — send it to friends **privately**. Anyone with the code can join.
To change the password, stop the server, edit `Password` in `%LOCALAPPDATA%\StembridgeValley-Server\host.json`, start it again, and send the new code.

## If friends can't connect

- Check **Server Status**: it should say "Running: yes".
- If it works on your own PC but not for friends, the router port isn't forwarded, or your internet provider shares one public address between customers (CGNAT). Ask and we'll switch to a tunnel.
