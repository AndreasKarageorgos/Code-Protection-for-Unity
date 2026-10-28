// =====================================================================
//  global-metadata.dat runtime decryptor  (IL2CPP / libil2cpp patch)
// =====================================================================
//
//  WHAT THIS IS
//  ------------
//  The editor post build step (MetadataBuildPostProcessor) encrypts
//  global-metadata.dat on disk with a keystream. The IL2CPP runtime then
//  tries to load the raw file, sees a scrambled header, and aborts. This
//  code reverses the keystream in memory as the file is loaded, so the
//  game boots normally while the file on disk stays unreadable to rippers
//  (Il2CppDumper and similar).
//
//  This is NOT a standalone plugin you can just drop in. The metadata is
//  loaded by a specific function inside Unity's own libil2cpp source, and
//  the only reliable place to decrypt is inside that function. You must
//  patch libil2cpp. The two functions to change are in:
//
//      <UnityEditor>/Data/il2cpp/libil2cpp/vm/MetadataLoader.cpp
//
//  on most Unity 2020 to 2023 and 6000 versions:
//
//      void* MetadataLoader::LoadMetadataFile(const char* fileName)
//      void  MetadataLoader::FreeMetadataFile(void* fileBuffer)   // name varies
//
//  IMPORTANT: the exact signature and body of LoadMetadataFile changes
//  between Unity versions. Open your version's file, find the function,
//  and adapt. Do not paste blindly. See the README for the automated
//  patch on build approach that backs up and restores these files.
//
//  The KEY below MUST match the "Passphrase" field in the Code Protection
//  panel. Change both together.
// =====================================================================

#include <cstdint>
#include <cstring>
#include <cstdlib>
#include <cstdio>

// ---- keystream: must match XorKeystream.cs exactly -------------------

#define CP_METADATA_PASSPHRASE "change-me"   // <-- keep in sync with the panel

static uint64_t cp_seed_from_passphrase(const char* s)
{
    uint64_t hash = 14695981039346656037ULL;  // FNV 1a 64 bit offset basis
    for (const char* p = s; *p; ++p)
    {
        hash ^= (uint8_t)*p;
        hash *= 1099511628211ULL;              // FNV 1a 64 bit prime
    }
    return hash == 0ULL ? 0x9E3779B97F4A7C15ULL : hash;
}

static void cp_apply_keystream(uint8_t* data, size_t len)
{
    uint64_t state = cp_seed_from_passphrase(CP_METADATA_PASSPHRASE);
    for (size_t i = 0; i < len; ++i)
    {
        // xorshift64*
        state ^= state >> 12;
        state ^= state << 25;
        state ^= state >> 27;
        uint64_t x = state * 0x2545F4914F6CDD1DULL;
        data[i] ^= (uint8_t)(x >> 56);
    }
}

// Only decrypt the real metadata file, not other resources loaded by the
// same function. Returns true for names ending in "global-metadata.dat".
static bool cp_is_metadata(const char* fileName)
{
    if (!fileName) return false;
    const char* needle = "global-metadata.dat";
    size_t fn = strlen(fileName), nd = strlen(needle);
    return fn >= nd && strcmp(fileName + (fn - nd), needle) == 0;
}

// ---------------------------------------------------------------------
//  Drop in replacement body for MetadataLoader::LoadMetadataFile.
//
//  The stock version memory maps the file read only, which we cannot write
//  to. This version reads it into a heap buffer and decrypts that buffer,
//  then returns it. Because it is malloc'd (not mmap'd), FreeMetadataFile
//  must free() it instead of unmapping. See below.
//
//  Paste the inside of this function into your LoadMetadataFile, keeping
//  the surrounding namespace and class. utils::Runtime::GetDataDir() and
//  utils::PathUtils::Combine are the il2cpp helpers already used there;
//  if your version builds the path differently, reuse its existing path
//  code and only add the read plus cp_apply_keystream part.
// ---------------------------------------------------------------------
/*
void* MetadataLoader::LoadMetadataFile(const char* fileName)
{
    std::string dir  = utils::PathUtils::Combine(
                           utils::Runtime::GetDataDir(),
                           utils::StringView<char>("Metadata", 8));
    std::string path = utils::PathUtils::Combine(
                           dir, utils::StringView<char>(fileName, strlen(fileName)));

    FILE* f = fopen(path.c_str(), "rb");
    if (!f) return NULL;

    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (size <= 0) { fclose(f); return NULL; }

    uint8_t* buffer = (uint8_t*)malloc((size_t)size);
    if (!buffer) { fclose(f); return NULL; }

    size_t read = fread(buffer, 1, (size_t)size, f);
    fclose(f);
    if (read != (size_t)size) { free(buffer); return NULL; }

    if (cp_is_metadata(fileName))
        cp_apply_keystream(buffer, (size_t)size);

    return buffer;
}
*/

// ---------------------------------------------------------------------
//  Matching FreeMetadataFile. The stock version unmaps; ours frees.
// ---------------------------------------------------------------------
/*
void MetadataLoader::FreeMetadataFile(void* fileBuffer)
{
    if (fileBuffer)
        free(fileBuffer);
}
*/

// If your Unity version keeps global-metadata.dat memory mapped elsewhere
// and you only want the minimal change, an alternative is to decrypt right
// after the existing MemoryMappedFile::Map call by copying the mapped region
// into a writable buffer. The read into heap approach above is the most
// portable and is what the README automated patcher applies.
