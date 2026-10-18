#!/bin/sh
# Installs WheelFix for Linux: copies it to /usr/local/lib/wheelfix, adds
# the wheelfix-tools command, writes the default /etc/wheelfix.ini if there
# is none, and starts the service, also at every boot. Run it again to
# update; /etc/wheelfix.ini is kept.
# Needs python3 and systemd, which mainstream distributions include.
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "WheelFix needs administrator rights to install."
    exec sudo sh "$0" "$@"
fi
for program in python3 systemctl; do
    if ! command -v "$program" >/dev/null 2>&1; then
        echo "WheelFix needs $program, which was not found."
        exit 1
    fi
done

SRC=$(dirname "$0")
DEST=/usr/local/lib/wheelfix

install -d -m 755 "$DEST"
install -m 644 "$SRC/wheelfilter.py" "$SRC/wheelfix.py" "$SRC/wheelfix_tools.py" \
    "$SRC/wheelfix.ini" "$DEST/"
printf '#!/bin/sh\nexec /usr/bin/python3 %s/wheelfix_tools.py "$@"\n' "$DEST" \
    > /usr/local/bin/wheelfix-tools
chmod 755 /usr/local/bin/wheelfix-tools
if [ ! -e /etc/wheelfix.ini ]; then
    install -m 644 "$SRC/wheelfix.ini" /etc/wheelfix.ini
fi
# The virtual mice need the uinput kernel module: load it now and at every
# boot. Many kernels only load it on demand, which does not always happen.
printf 'uinput\n' > /etc/modules-load.d/wheelfix.conf
if ! modprobe uinput; then
    echo "Warning: the uinput kernel module could not be loaded, and WheelFix"
    echo "cannot work without it. Check that your kernel provides it: modinfo uinput"
fi
install -m 644 "$SRC/wheelfix.service" /etc/systemd/system/wheelfix.service
systemctl daemon-reload
systemctl enable wheelfix.service >/dev/null
systemctl restart wheelfix.service

echo "WheelFix is installed and running, and it starts with the system."
echo "Settings:  sudoedit /etc/wheelfix.ini"
echo "Tools:     wheelfix-tools (calibrate, debug, replay, restore)"
