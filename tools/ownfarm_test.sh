#!/bin/bash
# Own-farm test on the ARM test instance (/opt/stardew/lt, port 24643). Run on the ARM box with sudo rights.
# usage: ownfarm_test.sh fresh|run <bot> <discordid> <character> [friendid]|stop <bot>|log|state
set -e
LT=/opt/stardew/lt
L=$LT/Roaming/StardewValley/ErrorLogs/SMAPI-latest.txt
SRV="sudo systemd-run --collect --uid=stardew --gid=stardew -p WorkingDirectory=/opt/stardew -E DOTNET_ROOT=/opt/stardew/dotnet -E HOME=/opt/stardew -E SDL_AUDIODRIVER=dummy "
CMD="/usr/bin/xvfb-run -a -s '-screen 0 1280x720x24' /opt/stardew/dotnet/dotnet /opt/stardew/host/SVHost.dll"

start_server() {
  sudo systemctl stop sv-loadtest 2>/dev/null || true; sleep 3; sudo systemctl reset-failed sv-loadtest 2>/dev/null || true
  eval $SRV --unit=sv-loadtest $CMD --instance $LT >/dev/null
  for i in $(seq 1 60); do sleep 3; sudo grep -aq "Farm is up" $L 2>/dev/null && break; done
}

case "$1" in
fresh)  # new world with N farms (default 2)
  sudo systemctl stop sv-loadtest 2>/dev/null || true; sleep 3
  ts=$(date +%Y%m%d-%H%M%S)
  sudo mkdir -p /opt/stardew/lt-old
  for d in Roaming/StardewValley/Saves state; do [ -e $LT/$d ] && sudo mv $LT/$d /opt/stardew/lt-old/$(basename $d)-$ts || true; done
  sudo find $LT/Roaming -name 'StembridgeValley*.json' -path '*globalData*' -exec mv {} /opt/stardew/lt-old/ \; 2>/dev/null || true
  sudo rm -rf $LT/Roaming/StardewValley/.smapi/mod-data/stembridge.stembridgevalley 2>/dev/null || true
  sudo -u stardew mkdir -p $LT/state
  sudo python3 - "$2" <<'EOF'
import json, sys
p = "/opt/stardew/lt/host.json"; h = json.load(open(p))
h["TestEnv"]["SV_FARM_COUNT"] = sys.argv[1] or "2"
json.dump(h, open(p, "w"), indent=2)
EOF
  start_server; sudo grep -a -E "farms:|Built cabin|Creating" $L | tail -8 ;;
restart) start_server; sudo grep -a -E "farms:|Removed|Lined up|Built cabin" $L | tail -12 ;;
run)    # run <bot> <discordid> <character> [inviterid]
  b=$2; id=$3; ch=$4; inv=${5:-}
  tok="tok${id}AAAAAAAAAAAAAAAA"
  sudo python3 - "$id" "$ch" "$inv" "$tok" <<'EOF'
import json, os, sys
id_, ch, inv, tok = sys.argv[1:]
p = "/opt/stardew/lt/state/discord-players.json"
d = json.load(open(p)) if os.path.exists(p) else {"tokens": {}, "revoked": []}
d["tokens"] = {k: v for k, v in d["tokens"].items() if v["id"] != id_}
d["tokens"][tok] = {"id": id_, "name": ch, "friend": inv or None}
json.dump(d, open(p, "w"), indent=2)
os.system(f"chown stardew: {p}")
EOF
  PW=$(sudo python3 -c 'import json;print(json.load(open("/opt/stardew/lt/host.json"))["Password"])')
  d=/opt/stardew/ltbots/$b
  sudo systemctl stop sv-$b 2>/dev/null || true; sudo systemctl reset-failed sv-$b 2>/dev/null || true
  sudo rm -rf $d; sudo -u stardew mkdir -p $d/state
  printf '{"Role":"client","Address":"127.0.0.1:24643","Password":"d-%s-%s","PlayerKey":"pc%sxxxxxxxxxx","GamePath":"/opt/stardew/game","Feed":"/opt/stardew/lt-feed/pack.json","AutoRestart":false,"TestEnv":{"SV_BOT":"1","SV_TEST_CHARACTER":"%s"}}' "$id" "$tok" "$id" "$ch" | sudo -u stardew tee $d/host.json >/dev/null
  eval $SRV --unit=sv-$b $CMD --instance $d >/dev/null
  for i in $(seq 1 50); do sleep 3; [ -s $d/state/bot.csv ] && sudo tail -1 $d/state/bot.csv | grep -q ',1,' && break; done
  printf "$b: "; sudo tail -1 $d/state/bot.csv | cut -d, -f2,3,11 ;;
stop) sudo systemctl stop sv-$2 2>/dev/null || true ;;
stopall) for u in $(systemctl list-units --plain --no-legend 'sv-fbot*' 'sv-o*' | awk '{print $1}'); do sudo systemctl stop $u; done
  sudo pkill -f 'SVHost.dll --instance /opt/stardew/ltbots/' || true; sudo systemctl stop sv-loadtest ;;
log) sudo grep -a -E "farms:|joins|moves|Built cabin|Removed|Lined up|Opened|invited|No farm|ERROR|Approved" $L | grep -v XACT | tail -${2:-20} ;;
state) sudo cat $LT/state/farm-members.json; echo; sudo cat $LT/state/farm-status.json | head -40 ;;
esac
