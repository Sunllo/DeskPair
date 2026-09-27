// A shell on a pseudo-terminal, started the way login_tty does it, in C.
//
// Why here and not in C#: making the pty the child's controlling terminal takes ioctl(TIOCSCTTY) in the
// child after setsid(), and on Apple silicon ioctl cannot be called from .NET at all -- it is variadic,
// and Apple's arm64 ABI passes variadic arguments on the stack where a P/Invoke puts them in registers.
// posix_spawn cannot do it either: it has no action that sets a controlling terminal.
//
// Why fork is acceptable here when the C# side must never fork: between fork() and execve() the child
// below calls only async-signal-safe functions (setsid, ioctl, dup2, close, sigaction, sigprocmask,
// chdir, execve, _exit) on memory prepared before the fork. It never touches the runtime, malloc, or
// Objective-C, which is what makes a fork of a multi-threaded process safe. The same pattern is how the
// .NET runtime itself starts processes on macOS.

#include "shim.h"
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/resource.h>
#include <sys/ttycom.h>
#include <unistd.h>

int fd_pty_spawn(const char* path, char* const argv[], char* const envp[], const char* cwd,
                 int columns, int rows, int* master_out, int* pid_out)
{
    int master = posix_openpt(O_RDWR | O_NOCTTY);
    if (master < 0) {
        return errno;
    }

    (void)fcntl(master, F_SETFD, FD_CLOEXEC);
    char name[128];
    if (grantpt(master) != 0 || unlockpt(master) != 0 || ioctl(master, TIOCPTYGNAME, name) != 0) {
        int e = errno;
        close(master);
        return e;
    }

    int slave = open(name, O_RDWR | O_NOCTTY | O_CLOEXEC);
    if (slave < 0) {
        int e = errno;
        close(master);
        return e;
    }

    // On the slave, once it is open: set on a master whose slave nobody has opened yet, macOS forgets it,
    // and the shell's first `stty size` reads 0 0.
    struct winsize size = { .ws_row = (unsigned short)rows, .ws_col = (unsigned short)columns };
    (void)ioctl(slave, TIOCSWINSZ, &size);

    // Everything the child needs is computed now: after fork it may not allocate or look anything up.
    struct rlimit limit;
    int max_fd = 4096;
    if (getrlimit(RLIMIT_NOFILE, &limit) == 0 && limit.rlim_cur != RLIM_INFINITY && limit.rlim_cur < 65536) {
        max_fd = (int)limit.rlim_cur;
    }

    sigset_t none;
    sigemptyset(&none);
    struct sigaction deflt;
    memset(&deflt, 0, sizeof deflt);
    deflt.sa_handler = SIG_DFL;

    pid_t pid = fork();
    if (pid == 0) {
        // The child. Async-signal-safe calls only, from here to execve.
        setsid();
        ioctl(slave, TIOCSCTTY, 0);
        dup2(slave, 0);
        dup2(slave, 1);
        dup2(slave, 2);
        for (int fd = 3; fd < max_fd; fd++) {
            close(fd);
        }

        // The runtime ignores SIGPIPE and handles others; a shell starts with every signal at default.
        for (int sig = 1; sig < NSIG; sig++) {
            sigaction(sig, &deflt, NULL);
        }
        sigprocmask(SIG_SETMASK, &none, NULL);

        if (cwd != NULL) {
            chdir(cwd);
        }

        execve(path, argv, envp);
        _exit(127);
    }

    int e = errno;
    close(slave);
    if (pid < 0) {
        close(master);
        return e;
    }

    *master_out = master;
    *pid_out = pid;
    return 0;
}

int fd_pty_resize(int master, int columns, int rows)
{
    struct winsize size = { .ws_row = (unsigned short)rows, .ws_col = (unsigned short)columns };
    return ioctl(master, TIOCSWINSZ, &size) == 0 ? 0 : errno;
}
