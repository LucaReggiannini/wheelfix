#!/bin/sh
# Removes WheelFix for Linux: stops the service and deletes its files, the
# wheelfix-tools command and /etc/wheelfix.ini.
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "WheelFix needs administrator rights to uninstall."
    exec sudo sh "$0" "$@"
fi

systemctl disable --now wheelfix.service >/dev/null 2>&1 || true
rm -f /etc/systemd/system/wheelfix.service
systemctl daemon-reload
rm -rf /usr/local/lib/wheelfix
rm -f /usr/local/bin/wheelfix-tools /etc/wheelfix.ini /etc/wheelfix.ini.bak \
    /etc/modules-load.d/wheelfix.conf

echo "WheelFix has been removed."
