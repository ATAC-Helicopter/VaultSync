#!/bin/sh
# Run only in a disposable, offline container built with Debian.Containerfile.
set -eu
test -f /run/.containerenv
test -d /inputs/installation
test -d /inputs/boot-config
test -d /output
test ! -e /output/recovery-root.tar
test ! -e /output/BOOTX64.EFI
test ! -e /usr/lib/vaultsync-recovery

manifest=/inputs/installation/recovery.installation.json
systemd_sha=$(sha256sum /usr/lib/systemd/systemd)
grep -Fq "\"systemdSha256\": \"${systemd_sha%% *}\"" "$manifest"
if grep -Fq '"controllerMode": "graphical"' "$manifest"; then
    xorg_sha=$(sha256sum /usr/lib/xorg/Xorg)
    grep -Fq "\"xorgSha256\": \"${xorg_sha%% *}\"" "$manifest"
    install -m 0644 /inputs/installation/boot/vaultsync-recovery-display.service /usr/lib/systemd/system/vaultsync-recovery-display.service
else
    grep -Fq '"controllerMode": "capture"' "$manifest"
fi

cp -a /inputs/installation /usr/lib/vaultsync-recovery
chown -R 0:0 /usr/lib/vaultsync-recovery
install -d -m 0700 /root
install -d /boot/grub /srv/vaultsync-recovery /srv/vaultsync-recovery-exports \
    /etc/cloud /etc/systemd/system/multi-user.target.wants /etc/systemd/journald.conf.d
install -m 0644 /inputs/boot-config/fstab /etc/fstab
install -m 0644 /inputs/boot-config/grub.cfg /boot/grub/grub.cfg
install -m 0644 /usr/lib/vaultsync-recovery/boot/vaultsync-recovery.service /usr/lib/systemd/system/vaultsync-recovery.service
ln -s /usr/lib/systemd/system/vaultsync-recovery.service /etc/systemd/system/multi-user.target.wants/vaultsync-recovery.service
touch /etc/cloud/cloud-init.disabled
for service in ssh.service ssh.socket systemd-networkd.service systemd-networkd.socket \
    systemd-networkd-wait-online.service networking.service NetworkManager.service \
    getty@tty1.service getty@tty2.service serial-getty@ttyS0.service getty-static.service \
    grub-common.service debug-shell.service; do
    systemctl --root=/ mask "$service"
done
awk -F: '{print $1 ":!:0:0:99999:7:::"}' /etc/passwd > /etc/shadow
chown root:shadow /etc/shadow
chmod 0640 /etc/shadow
: > /etc/machine-id
printf 'vaultsync-recovery\n' > /etc/hostname
printf '[Journal]\nStorage=volatile\nForwardToConsole=yes\nTTYPath=/dev/ttyS0\nMaxLevelConsole=info\n' \
    > /etc/systemd/journald.conf.d/60-recovery-console.conf

grub-mkstandalone -O x86_64-efi --modules='part_gpt fat ext2 normal linux search search_fs_uuid' \
    --locales='' --fonts='' -o /output/BOOTX64.EFI 'boot/grub/grub.cfg=/boot/grub/grub.cfg'
tar --one-file-system --numeric-owner --exclude='./inputs' --exclude='./output' \
    --exclude='./run' --exclude='./dev' --exclude='./proc' --exclude='./sys' \
    --exclude='./etc/hosts' --exclude='./etc/resolv.conf' --exclude='./.dockerenv' \
    --exclude='./var/lib/apt/lists' --exclude='./var/cache/apt/archives' \
    --exclude='./etc/shadow-' --exclude='./etc/gshadow-' --exclude='./root/.*' \
    -C / -cf /output/recovery-root.tar .
