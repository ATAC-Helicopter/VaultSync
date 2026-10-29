#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <grp.h>
#include <linux/capability.h>
#include <stdio.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/syscall.h>
#include <unistd.h>

#ifndef SOURCE_COMMIT
#error SOURCE_COMMIT must identify the reviewed source
#endif

/* Fixed installed role only. No supplied program, UID, path or environment. */
int main(int argc, char **argv)
{
    if (argc == 2 && strcmp(argv[1], "--installation-identity") == 0) {
        printf("{\"schemaVersion\":1,\"role\":\"recovery-controller-launch-v1\",\"runtimeIdentifier\":\"linux-x64\",\"sourceCommit\":\"%s\"}", SOURCE_COMMIT);
        return 0;
    }
    if (argc != 1 || getuid() != 0 || geteuid() != 0 || getgid() != 0 || getegid() != 0) return 77;
    /* Join the host-created delegated subgroup ourselves: write zero means the
       current task. No racing numeric PID is used for admission or cleanup. */
    int group = open("/sys/fs/cgroup/system.slice/vaultsync-recovery.service/controller/cgroup.procs", O_WRONLY | O_CLOEXEC | O_NOFOLLOW);
    if (group < 0) return 77;
    if (write(group, "0", 1) != 1) { close(group); return 77; }
    if (close(group) != 0 || setsid() < 0) return 77;
    if (setgroups(0, NULL) != 0 || setresgid(60999, 60999, 60999) != 0 || setresuid(60999, 60999, 60999) != 0) return 77;
    struct __user_cap_header_struct header = { _LINUX_CAPABILITY_VERSION_3, 0 };
    struct __user_cap_data_struct caps[2] = {{0}};
    if (syscall(SYS_capget, &header, caps) != 0 || caps[0].effective || caps[0].permitted || caps[0].inheritable ||
        caps[1].effective || caps[1].permitted || caps[1].inheritable || prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) != 0) return 77;
    if (syscall(SYS_close_range, 3U, ~0U, 0U) != 0) return 77;
    int null = open("/dev/null", O_RDWR | O_NOFOLLOW);
    if (null < 0) return 77;
    for (int fd = 0; fd < 3; ++fd) if (dup2(null, fd) < 0) return 77;
    if (null > 2 && close(null) != 0) return 77;
    char *const environment[] = { "LANG=C", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1", "DOTNET_EnableDiagnostics=0",
        "DOTNET_GCHeapHardLimit=0x10000000", "HOME=/usr/lib/vaultsync-recovery/controller",
        "TMPDIR=/usr/lib/vaultsync-recovery/controller", NULL };
    char *const arguments[] = { "/usr/lib/vaultsync-recovery/controller/VaultSync.RecoveryController", NULL };
    execve(arguments[0], arguments, environment);
    return errno == EACCES ? 77 : 74;
}
