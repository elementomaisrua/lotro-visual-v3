#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>
#include "../outputs/visual-interno-lotro-v3/nomes_lotro.h"
#include "../outputs/visual-interno-lotro-v3/tipos_lotro.h"
static unsigned tests;
static bool Check(bool ok,const char* label){++tests;if(!ok)fprintf(stderr,"FAILED: %s\n",label);return ok;}
int wmain(int argc,wchar_t** argv){
    if(argc!=2 || wcslen(argv[1])>900){fprintf(stderr,"Usage: test-auto-metadata <game-directory>\n");return 2;}
    wchar_t archivePath[1024]{},codecPath[1024]{};
    wcscpy(archivePath,argv[1]);wcscat(archivePath,L"\\client_local_English.dat");
    wcscpy(codecPath,argv[1]);wcscat(codecPath,L"\\x64\\zlib1.dll");
    bool ok=Check(LocalNames::Open(archivePath),"read-only archive");
    if(!ok)return 2;
    LocalNames::codec=LoadLibraryW(codecPath);
    if(LocalNames::codec)LocalNames::uncompress=(LocalNames::Uncompress)GetProcAddress(LocalNames::codec,"uncompress");
    struct Example{uint64_t key;const wchar_t* text;};
    Example examples[]={{0x2501bb330da44875,L"Cold-fanged Barghest"},{0x250060b90da44875,L"Shadowmaw Howler"},{0x25057b420da44875,L"Teak Branches"},{0x250060e10da44875,L"Ongb\u00farz Labourer"},{0x250060e00da44875,L"Ongb\u00farz Tracker"},{0x2500612b0da44875,L"Fallen Timber"}};
    for(auto e:examples){
        wchar_t name[96]{};ok&=Check(LocalNames::Lookup(e.key,name) && !wcscmp(name,e.text),"name from shipped archive");
        name[0]=0;ok&=Check(LocalNames::Lookup(e.key,name) && !wcscmp(name,e.text),"cached name");
    }
    // Exercise the exact parser used by the DLL against truncated records.
    const unsigned char* data=nullptr;unsigned length=0;
    if(!LocalNames::Content(0x2501bb33,&data,&length))return 2;
    for(unsigned i=0;i<length;++i){wchar_t name[96]{};uint32_t ref=0;ok&=Check(!LocalNames::Parse(data,i,0x2501bb33,0x0da44875,name,&ref),"truncated record rejected");}
    unsigned char copy[1024]{};memcpy(copy,data,length);copy[18]=2;
    wchar_t name[96]{};uint32_t ref=0;
    ok&=Check(!LocalNames::Parse(copy,length,0x2501bb33,0x0da44875,name,&ref),"malformed fragments rejected");
    struct Kind{uint32_t value;const char* expected;};
    Kind types[]={{0x1f,"jogador"},{0x3f,"jogador"},{0x1004f,"monstro"},{0x4f,"npc"},{0x2004f,"realnpc"},{0x4004f,"npc"},{0xa0081,"objeto"},{0x90081,"objeto"},{0xa00081,"objeto"},{0x20103,"objeto"},{0xf,"nao_classificado"},{0,"nao_classificado"},{0x12345678,"nao_classificado"}};
    for(auto t:types)ok&=Check(!strcmp(EntityKind(t.value,false),t.expected),"enum classification");
    ok&=Check(!strcmp(EntityKind(0x1004f,true),"jogador"),"player identity precedes monster type");
    LocalNames::Close();printf("{\"ok\":%s,\"checks\":%u,\"game_process_access\":false}\n",ok?"true":"false",tests);return ok?0:1;
}
