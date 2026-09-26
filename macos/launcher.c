/*
 * Study Stash's universal launcher.
 *
 * A .NET self-contained publish is per architecture, and the two published trees can't be merged with lipo
 * (System.Private.CoreLib and the R2R framework assemblies differ arm64 vs x64). So the app bundle carries both
 * trees whole, under Contents/MacOS/arm64 and Contents/MacOS/x64, and this tiny native program is the one thing
 * that's actually universal: it's Contents/MacOS/StudyStash, built `clang -arch arm64 -arch x86_64`, so macOS always
 * finds a slice for the Mac it's running on. Running a per-arch program with exec() from a subfolder was not an
 * option: Launch Services (LSUIElement, the microphone usage description, TCC) only recognizes an app whose running
 * executable sits directly in Contents/MacOS, so an exec'd Contents/MacOS/arm64/StudyStash would lose the bundle
 * identity and macOS would kill the app the first time it touched the microphone. Instead this launcher dlopens its
 * own architecture's libhostfxr.dylib and calls hostfxr_main_startupinfo to host .NET in-process, in the same
 * process macOS already knows as the bundle's executable. Environment.ProcessPath and AppContext.BaseDirectory then
 * both point at the per-arch tree, so Whisper's native libraries (runtimes/macos-<arch>) are found, and everything
 * that cares which program the OS launched (the login item, TCC, "one copy at a time") sees Contents/MacOS/StudyStash.
 */
#include <dlfcn.h>
#include <libgen.h>
#include <limits.h>
#include <mach-o/dyld.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef int (*startupinfo_fn)(int, const char **, const char *, const char *, const char *);

int main(int argc, char *argv[])
{
#if defined(__arm64__)
    const char *arch = "arm64";
#else
    const char *arch = "x64";
#endif
    char exe[PATH_MAX], real[PATH_MAX], dir[PATH_MAX], tree[PATH_MAX], fxr[PATH_MAX], host[PATH_MAX], app[PATH_MAX];
    uint32_t n = sizeof exe;
    if (_NSGetExecutablePath(exe, &n) != 0 || !realpath(exe, real))
    {
        fprintf(stderr, "Study Stash can't find its own path\n");
        return 111;
    }
    strncpy(dir, real, sizeof dir);
    dir[sizeof dir - 1] = '\0';
    snprintf(tree, sizeof tree, "%s/%s", dirname(dir), arch);
    snprintf(fxr, sizeof fxr, "%s/libhostfxr.dylib", tree);
    snprintf(host, sizeof host, "%s/StudyStash", tree);
    snprintf(app, sizeof app, "%s/StudyStash.dll", tree);

    void *h = dlopen(fxr, RTLD_NOW | RTLD_LOCAL);
    if (!h)
    {
        fprintf(stderr, "Study Stash can't start: %s\n", dlerror());
        return 112;
    }
    startupinfo_fn run = (startupinfo_fn)dlsym(h, "hostfxr_main_startupinfo");
    if (!run)
    {
        fprintf(stderr, "Study Stash can't start: hostfxr_main_startupinfo missing from %s\n", fxr);
        return 113;
    }
    return run(argc, (const char **)argv, host, tree, app);
}
