/* loadgen: a fixed, repeatable CPU load script for comparing sensor readings across OSes.
 *
 * The same source builds on Windows (MSVC) and Linux (gcc/clang), so HWiNFO on Windows and OpenHW on
 * Linux can watch identical load phases. Each phase prints a tagged line with a wall-clock timestamp so a
 * sensor log can be cut into phases afterwards.
 *
 *   Windows:  cl /O2 /arch:AVX2 loadgen.c
 *   Linux:    gcc -O2 -mavx2 -mfma -pthread loadgen.c -o loadgen
 *
 *   loadgen [seconds-per-phase]      (default 30)
 */
#include <immintrin.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#ifdef _WIN32
#include <windows.h>
typedef HANDLE thread_t;
static void sleep_s(int s) { Sleep((DWORD)s * 1000); }
static int ncpus(void) { return (int)GetActiveProcessorCount(ALL_PROCESSOR_GROUPS); }
#else
#include <pthread.h>
#include <unistd.h>
typedef pthread_t thread_t;
static void sleep_s(int s) { sleep((unsigned)s); }
static int ncpus(void) { return (int)sysconf(_SC_NPROCESSORS_ONLN); }
#endif

enum kind { SCALAR, AVX2 };

static volatile int stop;
static volatile double sink;

struct job { enum kind kind; };

/* Integer/scalar work: light current, high clocks. */
static void run_scalar(void)
{
    unsigned long long x = 88172645463325252ULL, acc = 0;
    while (!stop) {
        for (int i = 0; i < 1 << 20; i++) {
            x ^= x << 13; x ^= x >> 7; x ^= x << 17;
            acc += x;
        }
    }
    sink = (double)acc;
}

/* AVX2 FMA on registers only: heavy current, the classic Vcore droop case. */
static void run_avx2(void)
{
    __m256d a0 = _mm256_set1_pd(1.0), a1 = _mm256_set1_pd(1.1), a2 = _mm256_set1_pd(1.2),
            a3 = _mm256_set1_pd(1.3), a4 = _mm256_set1_pd(1.4), a5 = _mm256_set1_pd(1.5),
            a6 = _mm256_set1_pd(1.6), a7 = _mm256_set1_pd(1.7);
    const __m256d m = _mm256_set1_pd(0.9999999), c = _mm256_set1_pd(1e-7);
    while (!stop) {
        for (int i = 0; i < 1 << 20; i++) {
            a0 = _mm256_fmadd_pd(a0, m, c); a1 = _mm256_fmadd_pd(a1, m, c);
            a2 = _mm256_fmadd_pd(a2, m, c); a3 = _mm256_fmadd_pd(a3, m, c);
            a4 = _mm256_fmadd_pd(a4, m, c); a5 = _mm256_fmadd_pd(a5, m, c);
            a6 = _mm256_fmadd_pd(a6, m, c); a7 = _mm256_fmadd_pd(a7, m, c);
        }
    }
    __m256d s = _mm256_add_pd(_mm256_add_pd(_mm256_add_pd(a0, a1), _mm256_add_pd(a2, a3)),
                              _mm256_add_pd(_mm256_add_pd(a4, a5), _mm256_add_pd(a6, a7)));
    double out[4];
    _mm256_storeu_pd(out, s);
    sink = out[0] + out[1] + out[2] + out[3];
}

#ifdef _WIN32
static DWORD WINAPI worker(LPVOID p)
#else
static void *worker(void *p)
#endif
{
    if (((struct job *)p)->kind == AVX2) run_avx2(); else run_scalar();
    return 0;
}

static void phase(const char *name, int threads, enum kind kind, int secs)
{
    static struct job job;
    thread_t *t = calloc((size_t)(threads ? threads : 1), sizeof *t);
    time_t now = time(NULL);
    printf("PHASE %-14s threads=%-3d start_unix=%lld secs=%d\n", name, threads, (long long)now, secs);
    fflush(stdout);
    job.kind = kind;
    stop = 0;
    for (int i = 0; i < threads; i++) {
#ifdef _WIN32
        t[i] = CreateThread(NULL, 0, worker, &job, 0, NULL);
#else
        pthread_create(&t[i], NULL, worker, &job);
#endif
    }
    sleep_s(secs);
    stop = 1;
    for (int i = 0; i < threads; i++) {
#ifdef _WIN32
        WaitForSingleObject(t[i], INFINITE);
        CloseHandle(t[i]);
#else
        pthread_join(t[i], NULL);
#endif
    }
    free(t);
}

int main(int argc, char **argv)
{
    int secs = argc > 1 ? atoi(argv[1]) : 30;
    int n = ncpus();
    printf("loadgen: %d logical CPUs, %d s per phase\n", n, secs);
    phase("idle", 0, SCALAR, secs);
    phase("scalar-1t", 1, SCALAR, secs);
    phase("avx2-1t", 1, AVX2, secs);
    phase("scalar-8t", 8, SCALAR, secs);
    phase("scalar-all", n, SCALAR, secs);
    phase("avx2-all", n, AVX2, secs);
    phase("idle-after", 0, SCALAR, secs);
    printf("DONE end_unix=%lld\n", (long long)time(NULL));
    return 0;
}
