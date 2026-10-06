// Turns on rocksdb's io_uring support in the Linux libraries.
//
// Compiling rocksdb with -DROCKSDB_IOURING_PRESENT is not enough by itself:
// PosixFileSystem only hands out io_uring backed reads when the process
// defines this function and it returns true. rocksdb declares it weak and
// leaves it undefined everywhere except its own tests and db_bench, so a
// stock librocksdb.so linked against liburing still never uses it. A .NET
// host has no way to supply a C symbol of its own, so the library carries
// the definition itself. It stays exported, which leaves an executable that
// links rocksdb directly free to interpose its own.
//
// With it enabled, rocksdb uses io_uring for two things:
//
//   * MultiGet reads the data blocks it needs from one SST file in parallel
//     (PosixRandomAccessFile::MultiRead) instead of one pread after another.
//     This needs no option at all.
//   * Iterators read ahead asynchronously when ReadOptions.async_io is set.
//     Without io_uring that option is accepted but the reads stay synchronous.
//
// Whether the kernel allows io_uring is not decided here. PosixFileSystem's
// constructor sets up one ring with the flags it uses for every other one,
// and when that fails -- kernels before 6.1, Docker's default seccomp
// profile, kernel.io_uring_disabled -- it never tries again and every read
// stays on pread. That probe prints a single "CreateIOUring failed" line to
// stdout, which nothing outside rocksdb can suppress.
//
// ROCKSDB_SHARP_DISABLE_IO_URING set to anything but "" or "0" turns it off,
// for comparing the two or for working around a kernel problem. It is read
// with getenv, so it has to be in the environment the process was started
// with: .NET's Environment.SetEnvironmentVariable only changes the managed
// copy and is never seen here.

#include <cstdlib>
#include <cstring>

namespace {

bool DisabledByEnvironment() {
    const char* value = std::getenv("ROCKSDB_SHARP_DISABLE_IO_URING");
    return value != nullptr && value[0] != '\0' && std::strcmp(value, "0") != 0;
}

}  // namespace

extern "C" __attribute__((visibility("default"))) bool RocksDbIOUringEnable() {
    // rocksdb asks on every file it opens, so the answer is decided once. A
    // function local static is initialised exactly once even when several
    // threads get here at the same time.
    static const bool enabled = !DisabledByEnvironment();
    return enabled;
}
