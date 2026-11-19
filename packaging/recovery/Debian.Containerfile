# Pinned amd64 Debian root input for the offline media assembler.
FROM docker.io/library/debian@sha256:7378260c0a098d27ac206225db5e7d92b8c7c84ea7afccc86eeb513756bbd392

RUN set -eu; \
    test "$(dpkg --print-architecture)" = amd64; \
    printf '%s\n' \
      'deb [check-valid-until=no signed-by=/usr/share/keyrings/debian-archive-keyring.gpg] https://snapshot.debian.org/archive/debian/20261117T000000Z/ trixie main' \
      'deb [check-valid-until=no signed-by=/usr/share/keyrings/debian-archive-keyring.gpg] https://snapshot.debian.org/archive/debian-security/20261117T000000Z/ trixie-security main' \
      > /etc/apt/sources.list.d/recovery.list; \
    printf '%s\n' 'Dir::Etc::sourcelist "/etc/apt/sources.list.d/recovery.list";' \
      'Dir::Etc::sourceparts "-";' 'Acquire::https::CaInfo "/run/build-ca.pem";' \
      'Acquire::Retries "2";' 'Acquire::https::Timeout "30";' \
      > /etc/apt/apt.conf.d/99-recovery-build; \
    apt-get -o APT::Update::Error-Mode=any update; \
    cd /var/lib/apt/lists; \
    printf '%s\n' \
      '0584fba32e13e0ab8285fb16c27adea1ec03a73669c18702821094fd6ca86675  snapshot.debian.org_archive_debian_20261117T000000Z_dists_trixie_InRelease' \
      '49b95cf6911396794b2d12dd5e08c6c22216c7f532d1ac32b74db03c66610d06  snapshot.debian.org_archive_debian-security_20261117T000000Z_dists_trixie-security_InRelease' \
      | sha256sum -c -; \
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      systemd-sysv dbus libbpf1 udev linux-image-amd64 initramfs-tools grub-efi-amd64-bin \
      xserver-xorg-core xserver-xorg-input-libinput kbd libfontconfig1 \
      libicu76 libssl3t64 libx11-6 libxrandr2 libxcursor1 libxi6 libglib2.0-0t64; \
    dpkg-query -W -f='${binary:Package}\t${Version}\n' | LC_ALL=C sort > /usr/share/vaultsync-recovery-packages.tsv; \
    install -d /var/tmp; \
    apt-get clean
