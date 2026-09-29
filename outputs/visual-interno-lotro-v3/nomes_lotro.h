#pragma once
// Read-only lookup of the client's own localization records. Called only by
// our worker; no game functions, game cache mutation or external process read.
// Archive framing: https://github.com/Mirarkitty/lotro_mesh_viewer/blob/main/datfile.py
// Text schema verified in this build's
// serializers (RVA 5b9410, 5b9480, 352270), not a scan for name-shaped bytes.
namespace LocalNames {
static constexpr unsigned Limit=2*1024*1024;
static HANDLE archive=INVALID_HANDLE_VALUE;
static uint64_t fileSize;
static uint32_t directoryRoot;
static unsigned char* scratch;
static DWORD nextOpen;
static unsigned lookups, hits, misses;
static HMODULE codec;
using Uncompress=int (__cdecl *)(unsigned char*,unsigned long*,const unsigned char*,unsigned long);
static Uncompress uncompress;
struct Cached { uint64_t key; DWORD retry; wchar_t name[96]; };
static Cached cache[4096];

static uint32_t U32(const unsigned char* p) { uint32_t n;memcpy(&n,p,4);return n; }
static bool ReadAt(uint64_t at,void* out,unsigned n) {
    if(archive==INVALID_HANDLE_VALUE || at>fileSize || n>fileSize-at)return false;
    LARGE_INTEGER pos{};pos.QuadPart=(LONGLONG)at;DWORD got=0;
    return SetFilePointerEx(archive,pos,nullptr,FILE_BEGIN) && ReadFile(archive,out,n,&got,nullptr) && got==n;
}
static bool Open(const wchar_t* explicitPath=nullptr) {
    if(archive!=INVALID_HANDLE_VALUE)return true;
    DWORD now=GetTickCount();if(!explicitPath && nextOpen && (LONG)(now-nextOpen)<0)return false;
    nextOpen=now+5000;
    wchar_t path[1024]{};
    if(explicitPath){unsigned i=0;for(;explicitPath[i] && i<1023;++i)path[i]=explicitPath[i];if(explicitPath[i])return false;}
    else {
        unsigned length=GetModuleFileNameW(nullptr,path,1024);if(!length || length>=1024)return false;
        unsigned last=length;while(last && path[last-1]!=L'\\')--last;if(!last)return false;
        codec=GetModuleHandleW(L"zlib1.dll");
        if(codec)uncompress=(Uncompress)GetProcAddress(codec,"uncompress");
        --last;while(last && path[last-1]!=L'\\')--last;if(!last)return false;
        const wchar_t* name=L"client_local_English.dat";unsigned j=0;
        for(;name[j] && last+j<1023;++j)path[last+j]=name[j];path[last+j]=0;
    }
    HANDLE f=CreateFileW(path,GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(f==INVALID_HANDLE_VALUE)return false;
    LARGE_INTEGER size{};if(!GetFileSizeEx(f,&size) || size.QuadPart<512){CloseHandle(f);return false;}
    archive=f;fileSize=(uint64_t)size.QuadPart;unsigned char head[512]{};
    if(!ReadAt(0,head,sizeof(head)) || U32(head+0x101)!=0x4c50 || U32(head+0x140)!=0x5442){CloseHandle(archive);archive=INVALID_HANDLE_VALUE;return false;}
    directoryRoot=U32(head+0x160);
    if(!scratch)scratch=(unsigned char*)VirtualAlloc(nullptr,Limit*2,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);
    if(!scratch){CloseHandle(archive);archive=INVALID_HANDLE_VALUE;return false;}
    return true;
}
static bool Entry(uint32_t key,uint32_t* result) {
    uint32_t at=directoryRoot,seen[32]{};
    for(unsigned depth=0;depth<32 && at;++depth){
        for(unsigned i=0;i<depth;++i)if(seen[i]==at)return false;seen[depth]=at;
        unsigned char head[508]{};if(!ReadAt(at,head,sizeof(head)) || U32(head) || U32(head+4))return false;
        unsigned count=U32(head+504);if(count>61)return false;
        unsigned char entries[61*32]{};if(!ReadAt((uint64_t)at+508,entries,count*32))return false;
        unsigned branch=count;
        for(unsigned i=0;i<count;++i){
            uint32_t id=U32(entries+i*32+4);
            if(id==key){memcpy(result,entries+i*32,32);return true;}
            if(key<id){branch=i;break;}
        }
        if(!U32(head+8+branch*8))return false;at=U32(head+12+branch*8);
    }
    return false;
}
static bool Content(uint32_t key,const unsigned char** data,unsigned* length) {
    uint32_t e[8]{};if(!Open() || !Entry(key,e))return false;
    uint32_t size=e[3],block=e[6];if(size<4 || size>Limit || block<8 || block>Limit)return false;
    unsigned char head[8]{};if(!ReadAt(e[2],head,8))return false;
    unsigned extra=U32(head);if(U32(head+4) || extra>4096 || extra>(block-8)/8)return false;
    unsigned first=block-8-extra*8;if(first>size)first=size;
    if(!ReadAt((uint64_t)e[2]+8,scratch,first))return false;
    unsigned used=first;
    for(unsigned i=0;i<extra && used<size;++i){
        unsigned char part[8]{};if(!ReadAt((uint64_t)e[2]+8+first+i*8,part,8))return false;
        unsigned n=U32(part);if(n>size-used)n=size-used;
        if(!n || !ReadAt(U32(part+4),scratch+used,n))return false;used+=n;
    }
    if(used!=size)return false;
    *data=scratch;*length=size;
    if(e[0]&1){
        unsigned long expected=U32(scratch),actual=expected;
        if(!uncompress || expected<4 || expected>Limit || uncompress(scratch+Limit,&actual,scratch+4,size-4)!=0 || actual!=expected)return false;
        *data=scratch+Limit;*length=actual;
    }
    return U32(*data)==key;
}
struct Cursor {
    const unsigned char* data;unsigned size,at;bool ok;
    uint32_t Number(unsigned bytes) {
        if(!ok || at>size || bytes>size-at){ok=false;return 0;}
        uint32_t n=0;for(unsigned i=0;i<bytes;++i)n|=(uint32_t)data[at++]<<(i*8);return n;
    }
    uint32_t Vle() {
        unsigned a=Number(1);if(a==0xe0)return Number(4);if(!(a&0x80))return a;
        unsigned b=Number(1);if(!(a&0x40))return ((a&0x7f)<<8)|b;
        return ((a&0x3f)<<24)|(b<<16)|Number(2);
    }
    bool Text(wchar_t* out) {
        unsigned n=Vle();if(!ok || at>size || n>(size-at)/2){ok=false;return false;}
        bool usable=n>0 && n<96;
        if(out && usable){for(unsigned i=0;i<n;++i){out[i]=(wchar_t)(data[at+i*2]|data[at+i*2+1]<<8);if(out[i]<32)usable=false;}out[n]=0;}
        at+=n*2;return usable;
    }
};
static void CleanGrammar(wchar_t* name) {
    unsigned n=0;while(n<95 && name[n])++n;
    if(n<3 || name[n-1]!=L']')return;
    unsigned start=n-2;while(start && name[start]!=L'[' && n-start<=5)--start;
    if(!start || name[start]!=L'[')return;
    for(unsigned i=start+1;i<n-1;++i)if(name[i]!=L'm' && name[i]!=L'f' && name[i]!=L'n' && name[i]!=L'p' && name[i]!=L's' && name[i]!=L'v' && name[i]!=L'e')return;
    name[start]=0;
}
// A dynamic substitution must never be presented as if it were a complete name.
static bool Parse(const unsigned char* bytes,unsigned size,uint32_t table,uint32_t token,wchar_t* out,uint32_t* reference) {
    Cursor r{bytes,size,0,true};if(r.Number(4)!=table || r.Number(4)!=1)return false;
    r.Number(1);unsigned count=r.Vle();if(!r.ok || count>65536)return false;
    for(unsigned i=0;i<count && r.ok;++i){
        uint32_t key=r.Number(4),parent=r.Number(4);unsigned fragments=r.Number(4);if(fragments>4096)return false;
        wchar_t text[96]{};bool single=false;
        for(unsigned j=0;j<fragments && r.ok;++j){bool good=r.Text(j==0?text:nullptr);if(j==0)single=good;}
        unsigned arguments=r.Number(4);if(arguments>4096)return false;
        for(unsigned j=0;j<arguments && r.ok;++j)r.Number(4);
        unsigned alternate=r.Number(1);if(alternate>1)return false;
        if(alternate){unsigned alternatives=r.Number(4);if(alternatives>4096)return false;for(unsigned j=0;j<alternatives && r.ok;++j)r.Text(nullptr);}
        if(!r.ok)return false;
        if(key==token){
            if(parent){*reference=parent;return false;}
            if(fragments!=1 || arguments || !single)return false;
            CleanGrammar(text);if(!text[0])return false;memcpy(out,text,sizeof(text));return true;
        }
    }
    return false;
}
static bool Resolve(uint32_t table,uint32_t token,wchar_t* out,unsigned depth=0) {
    if(depth>=8 || (table>>24)!=0x25)return false;
    const unsigned char* data=nullptr;unsigned size=0;if(!Content(table,&data,&size))return false;
    uint32_t reference=0;if(Parse(data,size,table,token,out,&reference))return true;
    return reference && reference!=table && Resolve(reference,token,out,depth+1);
}
static bool Lookup(uint64_t key,wchar_t* out) {
    if(!key)return false;
    unsigned index=(unsigned)((key^(key>>32))*UINT64_C(11400714819323198485)>>52);
    Cached& item=cache[index];DWORD now=GetTickCount();
    if(item.key==key && (item.name[0] || (LONG)(now-item.retry)<0)){
        if(!item.name[0])return false;++hits;memcpy(out,item.name,sizeof(item.name));return true;
    }
    ++lookups;item.key=key;item.name[0]=0;item.retry=now+30000;
    if(!Resolve((uint32_t)(key>>32),(uint32_t)key,item.name)){++misses;return false;}
    memcpy(out,item.name,sizeof(item.name));return true;
}
static void Close() {
    if(archive!=INVALID_HANDLE_VALUE){CloseHandle(archive);archive=INVALID_HANDLE_VALUE;}
    if(scratch){VirtualFree(scratch,0,MEM_RELEASE);scratch=nullptr;}
    codec=nullptr;uncompress=nullptr;
}
}
