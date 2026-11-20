#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <grp.h>
#include <linux/capability.h>
#include <stdio.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/syscall.h>
#include <sys/stat.h>
#include <unistd.h>

#ifndef SOURCE_COMMIT
#error SOURCE_COMMIT must identify the reviewed source
#endif

static int report(const char *record)
{
    size_t length = strlen(record);
    return write(STDERR_FILENO, record, length) == (ssize_t)length;
}

/* Fixed installed role only. No supplied program, UID, path or environment. */
int main(int argc, char **argv)
{
    if (argc == 2 && strcmp(argv[1], "--installation-identity") == 0) {
        printf("{\"schemaVersion\":1,\"role\":\"recovery-controller-launch-v1\",\"runtimeIdentifier\":\"linux-x64\",\"sourceCommit\":\"%s\"}", SOURCE_COMMIT);
        return 0;
    }
    if (argc != 1 || getuid() != 0 || geteuid() != 0 || getgid() != 0 || getegid() != 0) return 77;
    /* The host creates this write-only pipe. It is never a console, socket or
       target/storage descriptor; only fixed status records may cross it. */
    struct stat status;
    int flags = fcntl(STDERR_FILENO, F_GETFL);
    if (flags < 0 || (flags & O_ACCMODE) != O_WRONLY || fstat(STDERR_FILENO, &status) != 0 ||
        !S_ISFIFO(status.st_mode) || (status.st_mode & 07777) != 0600 ||
        status.st_uid != 0 || status.st_gid != 0) return 77;
    if (!report("VSREC1|2|120|0\n") || !report("VSREC1|2|121|0\n")) return 77;
    /* Join the host-created delegated subgroup ourselves: write zero means the
       current task. No racing numeric PID is used for admission or cleanup. */
    int group = open("/sys/fs/cgroup/system.slice/vaultsync-recovery.service/controller/cgroup.procs", O_WRONLY | O_CLOEXEC | O_NOFOLLOW);
    if (group < 0) return 77;
    if (write(group, "0", 1) != 1) { close(group); return 77; }
    if (!report("VSREC1|2|122|0\n") || close(group) != 0 || setsid() < 0) return 77;
    if (!report("VSREC1|2|123|0\n")) return 77;
    if (setgroups(0, NULL) != 0 || setresgid(60999, 60999, 60999) != 0 || setresuid(60999, 60999, 60999) != 0) return 77;
    struct __user_cap_header_struct header = { _LINUX_CAPABILITY_VERSION_3, 0 };
    struct __user_cap_data_struct caps[2] = {{0}};
    if (!report("VSREC1|2|124|0\n")) return 77;
    if (syscall(SYS_capget, &header, caps) != 0 || caps[0].effective || caps[0].permitted || caps[0].inheritable ||
        caps[1].effective || caps[1].permitted || caps[1].inheritable || prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) != 0) return 77;
    if (!report("VSREC1|2|125|0\n") || syscall(SYS_close_range, 3U, ~0U, 0U) != 0) return 77;
    int null = open("/dev/null", O_RDWR | O_NOFOLLOW);
    if (null < 0) return 77;
    for (int fd = 0; fd < 2; ++fd) if (dup2(null, fd) < 0) return 77;
    if (null > 2 && close(null) != 0) return 77;
    char *const environment[] = { "LANG=C", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0", "DOTNET_EnableDiagnostics=0",
        "DOTNET_GCHeapHardLimit=0x10000000", "HOME=/usr/lib/vaultsync-recovery/controller",
        "DISPLAY=:0", "XAUTHORITY=/run/vaultsync-recovery-display/Xauthority",
        "TMPDIR=/run/vaultsync-recovery-display/controller", "XDG_CACHE_HOME=/run/vaultsync-recovery-display/controller", NULL };
    char *const arguments[] = { "/usr/lib/vaultsync-recovery/controller/VaultSync.RecoveryController", NULL };
    if (!report("VSREC1|2|126|0\n")) return 77;
    execve(arguments[0], arguments, environment);
    return errno == EACCES ? 77 : 74;
}
