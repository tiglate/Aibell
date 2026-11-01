#include <vector>
// Development tool for tests/fixtures/generate-alac-apple.sh (nothing of it is shipped): encodes raw s32le (left-justified) with Apple's ALACEncoder,
// built from Apple's published sources (Apache-2.0), to make streams FFmpeg cannot (20 and 32 bit, other frame sizes). Output: cookie size and bytes, then per packet frame count, byte count, bytes. usage: driver in.s32 out.alacpk channels bits rate [frameSize]
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "ALACEncoder.h"
#include "ALACAudioTypes.h"
int main(int argc, char** argv) {
    FILE* in = fopen(argv[1], "rb"); FILE* out = fopen(argv[2], "wb");
    uint32_t ch = atoi(argv[3]), bits = atoi(argv[4]), rate = atoi(argv[5]);
    uint32_t frameSize = argc > 6 ? atoi(argv[6]) : 4096;
    uint32_t bytes = bits == 16 ? 2 : (bits == 32 ? 4 : 3);
    AudioFormatDescription inF = {}, outF = {};
    inF.mSampleRate = rate; inF.mFormatID = kALACFormatLinearPCM;
    inF.mFormatFlags = kALACFormatFlagIsSignedInteger | kALACFormatFlagIsPacked;
    inF.mBitsPerChannel = bits; inF.mChannelsPerFrame = ch; inF.mBytesPerFrame = inF.mBytesPerPacket = ch * bytes; inF.mFramesPerPacket = 1;
    outF.mSampleRate = rate; outF.mFormatID = kALACFormatAppleLossless;
    outF.mFormatFlags = bits == 16 ? 1 : bits == 20 ? 2 : bits == 24 ? 3 : 4;
    outF.mFramesPerPacket = frameSize; outF.mChannelsPerFrame = ch;
    ALACEncoder enc; enc.SetFrameSize(frameSize); enc.InitializeEncoder(outF);
    uint32_t ck = enc.GetMagicCookieSize(ch); uint8_t* cookie = (uint8_t*)calloc(ck, 1); enc.GetMagicCookie(cookie, &ck);
    fwrite(&ck, 4, 1, out); fwrite(cookie, 1, ck, out);
    uint32_t perPacket = ch * bytes * frameSize;
    uint8_t* rd = (uint8_t*)calloc(perPacket, 1); uint8_t* wr = (uint8_t*)calloc(perPacket + 64, 1);
    std::vector<int32_t> raw(ch * frameSize);
    for (;;) {
        size_t got = fread(raw.data(), 4, ch * frameSize, in);
        if (got == 0) break;
        size_t n = got / ch; // frames in this packet
        for (size_t i = 0; i < got; i++) {
            int32_t s = raw[i]; uint8_t* p = rd + i * bytes;
            if (bits == 16) { p[0] = s >> 16; p[1] = s >> 24; }
            else if (bits == 20) { int32_t v = (s >> 12) << 4; p[0] = v; p[1] = v >> 8; p[2] = v >> 16; }
            else if (bits == 24) { p[0] = s >> 8; p[1] = s >> 16; p[2] = s >> 24; }
            else { p[0] = s; p[1] = s >> 8; p[2] = s >> 16; p[3] = s >> 24; }
        }
        int32_t nb = (int32_t)(n * ch * bytes);
        enc.Encode(inF, inF, rd, wr, &nb);
        uint32_t len = nb; uint32_t frames = (uint32_t)n;
        fwrite(&frames, 4, 1, out); fwrite(&len, 4, 1, out); fwrite(wr, 1, nb, out);
        if (got < ch * frameSize) break;
    }
    return 0;
}
