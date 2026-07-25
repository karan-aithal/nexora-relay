/* Minimal assert-based test harness — no framework, no allocation (CLAUDE.md: "No
 * frameworks, no fixtures"). Each test_*.c is its own executable; CHECK records failures
 * and TEST_SUMMARY returns non-zero if any failed, which CTest reads as failure. */
#ifndef OFP_TEST_H
#define OFP_TEST_H

#include <stdio.h>

static int g_checks = 0;
static int g_fails = 0;

#define CHECK(cond)                                                        \
    do                                                                     \
    {                                                                      \
        g_checks++;                                                        \
        if (!(cond))                                                       \
        {                                                                  \
            g_fails++;                                                     \
            fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); \
        }                                                                  \
    } while (0)

#define CHECK_EQ_U(actual, expected)                                                    \
    do                                                                                  \
    {                                                                                   \
        g_checks++;                                                                     \
        unsigned long long a_ = (unsigned long long)(actual);                           \
        unsigned long long e_ = (unsigned long long)(expected);                         \
        if (a_ != e_)                                                                   \
        {                                                                               \
            g_fails++;                                                                  \
            fprintf(stderr, "FAIL %s:%d: %s == %s (got %llu, want %llu)\n", __FILE__,   \
                    __LINE__, #actual, #expected, a_, e_);                              \
        }                                                                               \
    } while (0)

#define TEST_SUMMARY(name)                                                     \
    do                                                                         \
    {                                                                          \
        fprintf(stderr, "%s: %d checks, %d failed\n", (name), g_checks, g_fails); \
        return g_fails == 0 ? 0 : 1;                                           \
    } while (0)

#endif /* OFP_TEST_H */
