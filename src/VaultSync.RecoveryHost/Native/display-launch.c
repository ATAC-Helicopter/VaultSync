#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/random.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#ifndef SOURCE_COMMIT
#error SOURCE_COMMIT must identify the reviewed source
#endif

#define ROOT "/run/vaultsync-recovery-display"
static volatile sig_atomic_t stopping;
static void stop(int signal) { (void)signal; stopping = 1; }
static int same(const struct stat *a, const struct stat *b)
{ return a->st_dev == b->st_dev && a->st_ino == b->st_ino && a->st_mode == b->st_mode && a->st_uid == b->st_uid && a->st_gid == b->st_gid; }

/* Fixed boot-service role. Never adopts an existing runtime or deletes Xorg's
   global socket/lock. Only owned inode-checked entries are eligible for cleanup. */
int main(int argc, char **argv)
{
    if (argc == 2 && strcmp(argv[1], "--installation-identity") == 0) {
        printf("{\"schemaVersion\":1,\"role\":\"recovery-display-launch-v1\",\"runtimeIdentifier\":\"linux-x64\",\"sourceCommit\":\"%s\"}", SOURCE_COMMIT);
        return 0;
    }
    if (argc != 1 || getuid() != 0 || geteuid() != 0 || getgid() != 0 || getegid() != 0 || getppid() != 1) return 77;
    umask(0077);
    if (mkdir(ROOT, 0710) != 0) return 77;
    int root = open(ROOT, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (root < 0 || fchown(root, 0, 60999) != 0 || fchmod(root, 0710) != 0) return 77;
    struct stat owned, authority, temporary, current;
    if (fstat(root, &owned) != 0) return 77;
    int auth = openat(root, "Xauthority", O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0640);
    if (auth < 0 || fchown(auth, 0, 60999) != 0 || fchmod(auth, 0640) != 0) return 77;
    /* Xauthority FamilyWild, display 0, one 128-bit MIT-MAGIC-COOKIE-1 record. */
    unsigned char record[45] = { 255, 255, 0, 0, 0, 1, '0', 0, 18 };
    memcpy(record + 9, "MIT-MAGIC-COOKIE-1", 18);
    record[27] = 0; record[28] = 16;
    if (getrandom(record + 29, 16, 0) != 16 || write(auth, record, sizeof(record)) != sizeof(record) ||
        fsync(auth) != 0 || fstat(auth, &authority) != 0 || close(auth) != 0) return 77;
    memset(record, 0, sizeof(record));
    if (mkdirat(root, "controller", 0700) != 0) return 77;
    int tmp = openat(root, "controller", O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (tmp < 0 || fchown(tmp, 60999, 60999) != 0 || fstat(tmp, &temporary) != 0 || close(tmp) != 0) return 77;
    struct sigaction action = { .sa_handler = stop };
    if (sigemptyset(&action.sa_mask) != 0 || sigaction(SIGTERM, &action, NULL) != 0 || sigaction(SIGINT, &action, NULL) != 0) return 77;
    pid_t child = fork();
    if (child < 0) return 77;
    if (child == 0) {
        char *const arguments[] = { "/usr/lib/xorg/Xorg", ":0", "vt2", "-keeptty", "-nolisten", "tcp", "-nolisten", "local",
            "-auth", ROOT "/Xauthority", "-noreset", "-nopn", "-logfile", ROOT "/Xorg.log", NULL };
        char *const environment[] = { "PATH=/usr/sbin:/usr/bin:/sbin:/bin", "LANG=C", "HOME=/", NULL };
        execve(arguments[0], arguments, environment);
        _exit(74);
    }
    int status = 0;
    for (;;) {
        /* This is our unreaped child; its PID cannot be recycled here. */
        if (stopping && kill(child, SIGTERM) != 0 && errno != ESRCH) return 74;
        /* Polling avoids a lost-stop race between the flag check and a blocking
           waitpid. Never reap before the last possible signal to this child. */
        pid_t result = waitpid(child, &status, WNOHANG);
        if (result == child) break;
        if (result < 0 && errno == EINTR) continue;
        if (result < 0) return 74; // Retain resources if child exit is uncertain.
        struct timespec pause = { .tv_sec = 0, .tv_nsec = 100000000 };
        (void)nanosleep(&pause, NULL);
    }
    /* Xorg has exited. An unexpected/changed entry or controller cache is
       retained; no recursive deletion and no pathname-based process sweep. */
    int clean = lstat(ROOT, &current) == 0 && same(&owned, &current);
    if (clean && fstatat(root, "Xauthority", &current, AT_SYMLINK_NOFOLLOW) == 0 && same(&authority, &current))
        clean = unlinkat(root, "Xauthority", 0) == 0;
    else clean = 0;
    if (clean && fstatat(root, "controller", &current, AT_SYMLINK_NOFOLLOW) == 0 && same(&temporary, &current))
        clean = unlinkat(root, "controller", AT_REMOVEDIR) == 0;
    else clean = 0;
    if (close(root) != 0) clean = 0;
    /* Xorg.log and uncertain runtime contents are private /run evidence and are
       deliberately retained until reboot; they never become a new run's input. */
    int expected = (WIFEXITED(status) && WEXITSTATUS(status) == 0) ||
        (stopping && WIFSIGNALED(status) && WTERMSIG(status) == SIGTERM);
    return clean && expected ? 0 : 74;
}
