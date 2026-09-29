#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
static bool ReadLocal(uintptr_t p,void* out,size_t n){SIZE_T got=0;return p>=0x10000 && ReadProcessMemory(GetCurrentProcess(),(void*)p,out,n,&got) && got==n;}
template<class T>static T ReadValue(uintptr_t p){T v{};ReadLocal(p,&v,sizeof(v));return v;}
template<class T>static T Field(const unsigned char* p,unsigned o){T v{};memcpy(&v,p+o,sizeof(v));return v;}
#include "../outputs/visual-interno-lotro-v3/velocidade_lotro.h"
template<class T>static void Set(unsigned char* p,unsigned o,T v){memcpy(p+o,&v,sizeof(v));}
static unsigned entityBytes[80],componentBytes[100],nodeBytes[8],descriptorBytes[8];
static uintptr_t table[1];
int main(){
    auto* e=(unsigned char*)entityBytes;auto* c=(unsigned char*)componentBytes;auto* n=(unsigned char*)nodeBytes;auto* d=(unsigned char*)descriptorBytes;
    Set<uint64_t>(e,0x118,123);Set<uintptr_t>(e,0xc0,(uintptr_t)c);
    table[0]=(uintptr_t)n;Set<uintptr_t>(c,0x48,(uintptr_t)table);Set<unsigned>(c,0x58,1);
    Set<unsigned>(n,0,0x241);Set<uintptr_t>(n,16,(uintptr_t)d);Set<unsigned>(d,0x14,9);Set<float>(n,24,1.f);
    Movement::entity=(uintptr_t)e;Movement::id=123;
    unsigned checks=0;bool ok=true;
#define CHECK(x) do{++checks;if(!(x)){ok=false;printf("Failed line %d\n",__LINE__);}}while(0)
    uint64_t now=Movement::Now();Movement::Request r{0x53564c43,1,999,now+600000000,1,1.5f};
    CHECK(Movement::ValidRequest(r,999,now));
    CHECK(!Movement::ValidRequest(r,998,now));CHECK(!Movement::ValidRequest(r,999,r.expires));
    auto bad=r;bad.expires=now+1200000001;CHECK(!Movement::ValidRequest(bad,999,now));
    bad=r;bad.multiplier=4;CHECK(!Movement::ValidRequest(bad,999,now));bad.multiplier=.5f;CHECK(!Movement::ValidRequest(bad,999,now));
    Movement::Accept(r,999,now);CHECK(Movement::applied && ReadValue<float>((uintptr_t)n+24)==1.5f && Movement::writes==1);
    Movement::Accept(r,999,now);CHECK(Movement::writes==1);
    Movement::Restore("test");CHECK(!Movement::applied && ReadValue<float>((uintptr_t)n+24)==1.f);
    ++r.serial;Movement::Accept(r,999,now);Set<float>(n,24,1.25f);Movement::Restore("test");CHECK(ReadValue<float>((uintptr_t)n+24)==1.25f);
    Set<unsigned>(d,0x14,13);unsigned before=Movement::writes;++r.serial;Movement::Accept(r,999,now);CHECK(Movement::writes==before && !Movement::applied);
    Set<unsigned>(d,0x14,9);Set<unsigned>(c,0x58,0);++r.serial;Movement::Accept(r,999,now);CHECK(Movement::writes==before);
    Set<unsigned>(c,0x58,1);Set<uint64_t>(e,0x118,456);++r.serial;Movement::Accept(r,999,now);CHECK(Movement::writes==before);
    Set<uint64_t>(e,0x118,123);Set<float>(n,24,1.f);++r.serial;Movement::Accept(r,999,now);CHECK(Movement::applied);
    Movement::expires=now-1;Movement::Tick(123,999);CHECK(!Movement::applied && ReadValue<float>((uintptr_t)n+24)==1.f);
    printf("{\"ok\":%s,\"checks\":%u,\"writes_only_to_test_process\":true}\n",ok?"true":"false",checks);return ok?0:1;
}
