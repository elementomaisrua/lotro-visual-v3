#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <stdint.h>
#include <stddef.h>
#include "tipos_lotro.h"

// Payload freestanding: apenas KERNEL32 no import table. USER32 e GDI32
// sao resolvidas nas DLLs que ja estao carregadas no processo do jogo.
extern "C" { DWORD _tls_index = 0; }
extern "C" void* memset(void* p, int c, size_t n) {
    auto* d = (volatile unsigned char*)p;
    for (size_t i = 0; i < n; ++i) d[i] = (unsigned char)c;
    return p;
}
extern "C" void* memcpy(void* p, const void* q, size_t n) {
    auto* d = (volatile unsigned char*)p;
    auto* s = (const volatile unsigned char*)q;
    for (size_t i = 0; i < n; ++i) d[i] = s[i];
    return p;
}

#include "nomes_lotro.h"

#define UI_FUNCTIONS(X) \
 X(EnumWindows) X(GetWindowThreadProcessId) X(GetClientRect) X(ClientToScreen) \
 X(IsWindowVisible) X(IsIconic) X(IsWindow) X(GetWindow) X(GetForegroundWindow) \
 X(RegisterClassExW) X(UnregisterClassW) X(CreateWindowExW) X(DestroyWindow) \
 X(DefWindowProcW) X(SetLayeredWindowAttributes) X(SetWindowPos) X(ShowWindow) \
 X(PeekMessageW) X(TranslateMessage) X(DispatchMessageW) X(InvalidateRect) \
 X(BeginPaint) X(EndPaint) X(GetDC) X(ReleaseDC) X(FillRect)
#define GDI_FUNCTIONS(X) \
 X(CreateCompatibleDC) X(DeleteDC) X(CreateCompatibleBitmap) X(SelectObject) \
 X(DeleteObject) X(CreatePen) X(CreateSolidBrush) X(GetStockObject) X(Ellipse) \
 X(BitBlt) X(SetBkMode) X(SetTextColor) X(TextOutW) X(MoveToEx) X(LineTo)
#define DECLARE_API(name) static decltype(&name) p##name;
UI_FUNCTIONS(DECLARE_API)
GDI_FUNCTIONS(DECLARE_API)
#undef DECLARE_API

static bool LoadDrawingApis() {
    HMODULE user = GetModuleHandleW(L"user32.dll");
    if (!user) return false;
    HMODULE gdi = GetModuleHandleW(L"gdi32.dll");
    if (!gdi) return false;
#define LOAD_USER(name) p##name = (decltype(p##name))GetProcAddress(user, #name); if (!p##name) return false;
    UI_FUNCTIONS(LOAD_USER)
#undef LOAD_USER
#define LOAD_GDI(name) p##name = (decltype(p##name))GetProcAddress(gdi, #name); if (!p##name) return false;
    GDI_FUNCTIONS(LOAD_GDI)
#undef LOAD_GDI
    return true;
}

static uintptr_t moduleBase;
static HANDLE stopEvent, enabledEvent, readyEvent, singleInstance;
static HINSTANCE imageInstance;
static HWND gameWindow, overlayWindow;
static HDC bufferDc;
static HBITMAP bufferBitmap, oldBitmap;
static HPEN greenPen;
static HBRUSH blackBrush;
static int bufferWidth, bufferHeight;
static bool displayed, circlesEnabled = true;
static wchar_t outputDir[MAX_PATH], statusPath[MAX_PATH], eventName[96], enabledName[96], readyName[96], className[96];
static uint64_t processCreationTime;
static unsigned long frames, paintedFrames, actorsCount, circlesCount, readFailures;
static unsigned long long positionChanges, cameraChanges;
static DWORD lastErrorCode;

#pragma pack(push,1)
struct VisualOptions {
    uint32_t magic, version;
    uint64_t creation;
    uint32_t revision, style, maxPixels;
    float range;
    uint32_t labels, restricted;
    uint64_t menuWindow, focusId;
    uint32_t count;
    uint64_t ids[512];
    wchar_t customNames[512][96];
};
#pragma pack(pop)
static_assert(sizeof(VisualOptions)==102460,"Options protocol layout mismatch");
static VisualOptions options, pendingOptions;
static wchar_t optionsPath[MAX_PATH];
static HPEN shadowPen, selectedPen;
static void ReadOptions() {
    HANDLE file=CreateFileW(optionsPath,GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file==INVALID_HANDLE_VALUE)return;
    DWORD read=0;
    bool ok=ReadFile(file,&pendingOptions,sizeof(pendingOptions),&read,nullptr)!=FALSE;
    CloseHandle(file);
    if(!ok || read!=sizeof(pendingOptions) || pendingOptions.magic!=0x33564c43 || pendingOptions.version!=3 ||
       pendingOptions.creation!=processCreationTime || pendingOptions.count>512 || pendingOptions.style>2 ||
       pendingOptions.maxPixels<20 || pendingOptions.maxPixels>220 || !(pendingOptions.range>=10 && pendingOptions.range<=120))return;
    for(unsigned i=0;i<pendingOptions.count;++i)pendingOptions.customNames[i][95]=0;
    options=pendingOptions;
}
static bool Allowed(uint64_t id) {
    if(options.focusId && id!=options.focusId)return false;
    if(!options.restricted)return true;
    for(unsigned i=0;i<options.count;++i)if(options.ids[i]==id)return true;
    return false;
}

// Nunca abre outro processo: o pseudohandle refere-se ao proprio hospedeiro.
// RPM aqui fornece copia tolerante a ponteiros expirados durante trocas de mapa.
static bool ReadLocal(uintptr_t a, void* out, size_t n) {
    if (a < 0x10000 || a > UINT64_C(0x7fffffffffff) - n) return false;
    SIZE_T got = 0;
    bool ok = ReadProcessMemory(GetCurrentProcess(), (const void*)a, out, n, &got) && got == n;
    if (!ok) ++readFailures;
    return ok;
}
template<class T> static T ReadValue(uintptr_t a) {
    T x{};
    ReadLocal(a, &x, sizeof(x));
    return x;
}
template<class T> static T Field(const unsigned char* data, unsigned offset) {
    T x{};
    memcpy(&x, data + offset, sizeof(x));
    return x;
}
static float Abs(float x) { return x < 0 ? -x : x; }
static float Min(float a, float b) { return a < b ? a : b; }
static float Max(float a, float b) { return a > b ? a : b; }
static bool Finite(float x) { return x == x && Abs(x) < 10000000.0f; }

struct Vec { float x, y, z; };
static Vec Add(Vec a, Vec b) { return {a.x+b.x, a.y+b.y, a.z+b.z}; }
static Vec Sub(Vec a, Vec b) { return {a.x-b.x, a.y-b.y, a.z-b.z}; }
static Vec Scale(Vec a, float s) { return {a.x*s, a.y*s, a.z*s}; }
static float Dot(Vec a, Vec b) { return a.x*b.x+a.y*b.y+a.z*b.z; }
static Vec Cross(Vec a, Vec b) { return {a.y*b.z-a.z*b.y, a.z*b.x-a.x*b.z, a.x*b.y-a.y*b.x}; }
static bool Valid(Vec a) { return Finite(a.x) && Finite(a.y) && Finite(a.z); }
struct Quaternion { float w,x,y,z; };
static Vec Rotate(Quaternion q, Vec v) {
    Vec u{q.x,q.y,q.z};
    Vec t = Scale(Cross(u,v),2);
    return Add(v,Add(Scale(t,q.w),Cross(u,t)));
}
struct Position {
    uint64_t vtable;
    uint32_t valid;
    uint8_t blockX,blockY;
    uint16_t region;
    uint16_t cell;
    unsigned char padding[6];
    Vec xyz;
    Quaternion rotation;
    uint32_t padding2;
};
static_assert(sizeof(Position) == 0x38, "Layout Position x64");
static_assert(offsetof(Position,xyz) == 0x18, "Posicao no componente");
struct Camera {
    uint32_t valid;
    uint8_t blockX,blockY;
    uint16_t region;
    Vec right, forward, up, xyz;
    float fov;
};
static Camera camera;
static bool cameraValid;
static bool ReadCamera(Camera* result) {
    uintptr_t root = ReadValue<uintptr_t>(moduleBase + 0x1e52bb0);
    unsigned char data[0xb0];
    if (!root || !ReadLocal(root + 0x20, data, sizeof(data))) return false;
    Camera c{};
    c.valid = Field<uint32_t>(data,8);
    c.blockX = data[0xc]; c.blockY = data[0xd];
    c.region = Field<uint16_t>(data,0xe);
    c.right = Field<Vec>(data,0x20); c.forward = Field<Vec>(data,0x30);
    c.up = Field<Vec>(data,0x40); c.xyz = Field<Vec>(data,0x50);
    c.fov = Field<float>(data,0xa4);
    if (c.valid != 1 || !Valid(c.xyz) || !Valid(c.right) || !Valid(c.forward) || !Valid(c.up) ||
        !Finite(c.fov) || c.fov < 20 || c.fov > 120) return false;
    if (Abs(Dot(c.right,c.right)-1) > .04f || Abs(Dot(c.forward,c.forward)-1) > .04f ||
        Abs(Dot(c.up,c.up)-1) > .04f || Abs(Dot(c.right,c.forward)) > .04f ||
        Abs(Dot(c.right,c.up)) > .04f || Abs(Dot(c.forward,c.up)) > .04f) return false;
    *result = c;
    return true;
}

struct Actor {
    uint64_t id;
    uintptr_t entity, render, model;
    Position last;
    bool hadPosition;
    bool nearby, onScreen, observedSelectable;
    float distance;
    wchar_t name[96];
    uint64_t nameKey;
    uint32_t weenieType;
    bool isPlayer, typeKnown;
    unsigned nameSource;
};
static Actor actors[512];
static uintptr_t buckets[8192];
static uint64_t localPlayerId, selectedId;
static uint64_t observedIds[512];
static unsigned observedCount;

static bool PropertyIn(uintptr_t array,unsigned count,uint32_t key,unsigned delta,bool cached,unsigned expectedType,uintptr_t* value) {
    if(!array || !count || count>65536)return false;
    uintptr_t node=ReadValue<uintptr_t>(array+(key%count)*8);
    for(unsigned i=0;node && i<128;++i){
        unsigned char data[40]{};if(!ReadLocal(node,data,sizeof(data)))return false;
        if(Field<uint32_t>(data,0)==key){
            if(cached && !data[16])return false;
            uintptr_t descriptor=Field<uintptr_t>(data,delta);
            if(!descriptor || ReadValue<unsigned>(descriptor+0x14)!=expectedType)return false;
            *value=Field<uintptr_t>(data,delta+8);return true;
        }
        uintptr_t next=Field<uintptr_t>(data,8);if(next==node)return false;node=next;
    }
    return false;
}
static bool Property(uintptr_t component,uint32_t key,unsigned type,uintptr_t* value) {
    if(!component)return false;
    if(PropertyIn(ReadValue<uintptr_t>(component+0x140),ReadValue<unsigned>(component+0x150),key,0x18,true,type,value))return true;
    if(PropertyIn(ReadValue<uintptr_t>(component+0x48),ReadValue<unsigned>(component+0x58),key,0x10,false,type,value))return true;
    uintptr_t parent=ReadValue<uintptr_t>(component+0x98);
    return parent && PropertyIn(ReadValue<uintptr_t>(parent+0x40),ReadValue<unsigned>(parent+0x50),key,0x10,false,type,value);
}
#include "velocidade_lotro.h"
static bool CopyDisplayName(uintptr_t pointer,wchar_t* out) {
    if(!pointer)return false;
    unsigned length=ReadValue<unsigned>(pointer-4);
    if(length<2 || length>96)return false;
    wchar_t text[96]{};
    if(!ReadLocal(pointer,text,length*sizeof(wchar_t)))return false;
    for(unsigned i=0;i<length;++i){
        if(text[i]==0){if(!i)return false;memcpy(out,text,(i+1)*sizeof(wchar_t));return true;}
        if(text[i]<32 || (text[i]>=0xd800 && text[i]<=0xdfff))return false;
    }
    return false;
}
static void ReadMetadata(Actor& actor) {
    uintptr_t component=ReadValue<uintptr_t>(actor.entity+0xc0),value=0;
    if(!component)return;
    actor.typeKnown=Property(component,0x126,8,&value);actor.weenieType=actor.typeKnown?(uint32_t)value:0;
    actor.isPlayer=Property(component,0x47d,21,&value) && (value&255)==1;
    bool found=Property(component,0x354,13,&value);
    if(!found || ReadValue<uintptr_t>(value)!=moduleBase+0x14ee190)return;
    unsigned char info[0x58]{};if(!ReadLocal(value+0x10,info,sizeof(info)))return;
    uint32_t stringId=Field<uint32_t>(info,8),tableId=Field<uint32_t>(info,12);
    if(stringId && (tableId>>24)==0x25)actor.nameKey=((uint64_t)tableId<<32)|stringId;
    if(info[0x40]==1)CopyDisplayName(Field<uintptr_t>(info,0x38),actor.name);
    if(!actor.name[0])CopyDisplayName(Field<uintptr_t>(info,0),actor.name);
    if(actor.name[0])actor.nameSource=1;
    else if(LocalNames::Lookup(actor.nameKey,actor.name))actor.nameSource=2;
}

static void EnumerateActors() {
    actorsCount = 0;
    localPlayerId = ReadValue<uint64_t>(moduleBase + 0x1e42748);
    Movement::entity=0;Movement::id=localPlayerId;
    uintptr_t manager = ReadValue<uintptr_t>(moduleBase + 0x1e41f38);
    unsigned char table[24];
    if (!manager || !ReadLocal(manager+0x18,table,sizeof(table))) return;
    uintptr_t bucketArray = Field<uintptr_t>(table,0);
    unsigned count = Field<unsigned>(table,0x10);
    if (!count || count > 8192 || !ReadLocal(bucketArray,buckets,count*8)) return;
    unsigned visited = 0;
    for (unsigned i=0;i<count && visited<30000 && actorsCount<512;++i) {
        uintptr_t node = buckets[i];
        for (unsigned chain=0; node && chain<256 && actorsCount<512; ++chain) {
            struct Node { uint64_t id; uintptr_t next, entity; } n{};
            if (!ReadLocal(node,&n,sizeof(n))) break;
            ++visited;
            if(n.id==localPlayerId && ReadValue<uint64_t>(n.entity+0x118)==n.id)Movement::entity=n.entity;
            if (n.id && n.id!=localPlayerId) {
                unsigned char entity[0x120];
                if (ReadLocal(n.entity,entity,sizeof(entity)) && Field<uint64_t>(entity,0x118)==n.id &&
                    Field<uintptr_t>(entity,0x90)!=0) {
                    Actor a{};
                    a.id=n.id; a.entity=n.entity;
                    a.render=Field<uintptr_t>(entity,8); a.model=Field<uintptr_t>(entity,0x40);
                    if (a.render && ReadValue<uintptr_t>(a.render+0x18)==n.entity &&
                        1){  // render vtable check removed
                        ReadMetadata(a);actors[actorsCount++]=a;
                    }
                }
            }
            if (n.next==node) break;
            node=n.next;
        }
    }
}
// A selecao e consultada apenas para registrar o teste, nunca filtra circulos.
static void ReadSelectionForReport() {
    selectedId=0;
    uintptr_t manager=ReadValue<uintptr_t>(moduleBase+0x1e5b460);
    if (!manager || !localPlayerId) return;
    unsigned count=ReadValue<unsigned>(manager+0x60);
    uintptr_t array=ReadValue<uintptr_t>(manager+0x50);
    if (!count || count>65536 || !array) return;
    unsigned hash=(unsigned)localPlayerId+(unsigned)(localPlayerId>>32);
    uintptr_t node=ReadValue<uintptr_t>(array+(hash%count)*8);
    for (unsigned i=0;node && i<256;++i) {
        uint64_t n[3]{};
        if (!ReadLocal(node,n,sizeof(n))) return;
        if (n[0]==localPlayerId) { selectedId=n[2]; return; }
        if (n[1]==node) return;
        node=n[1];
    }
}

struct Circle { int x,y,r; uint64_t id; Vec relative; int halfW,halfH; unsigned actorIndex; };
static Circle circles[512];
static float focal;
static float CotHalfFov(float degrees) {
    const float x=degrees*.008726646259971648f;
    const float xx=x*x;
    const float s=x*(1-xx/6+xx*xx/120-xx*xx*xx/5040+xx*xx*xx*xx/362880);
    const float c=1-xx/2+xx*xx/24-xx*xx*xx/720+xx*xx*xx*xx/40320;
    return c/s;
}
static bool Project(Vec relative, float* x, float* y) {
    float depth=Dot(relative,camera.forward);
    if (!(depth>.20f)) return false;
    *x=(float)bufferWidth*.5f+Dot(relative,camera.right)*focal/depth;
    *y=(float)bufferHeight*.5f-Dot(relative,camera.up)*focal/depth;
    return Finite(*x) && Finite(*y);
}
static void UpdateCircles() {
    circlesCount=0;
    for(unsigned i=0;i<actorsCount;++i){actors[i].nearby=false;actors[i].onScreen=false;}
    Camera next{};
    cameraValid=ReadCamera(&next);
    if (!cameraValid) return;
    if (Dot(Sub(next.xyz,camera.xyz),Sub(next.xyz,camera.xyz))>.00001f ||
        Dot(Sub(next.forward,camera.forward),Sub(next.forward,camera.forward))>.000001f) ++cameraChanges;
    camera=next;
    focal=(float)bufferHeight*.5f*CotHalfFov(camera.fov);
    for (unsigned i=0;i<actorsCount;++i) {
        Actor& a=actors[i];
        if (ReadValue<uint64_t>(a.entity+0x118)!=a.id || ReadValue<uintptr_t>(a.render+0x18)!=a.entity) continue;
        Position pos{};
        if (!ReadLocal(a.render+0x98,&pos,sizeof(pos)) ||
            pos.valid!=1 || pos.region!=camera.region || !Valid(pos.xyz)) continue; // pos vtable check removed
        if (a.hadPosition && (pos.blockX!=a.last.blockX || pos.blockY!=a.last.blockY ||
            Dot(Sub(pos.xyz,a.last.xyz),Sub(pos.xyz,a.last.xyz))>.00001f)) ++positionChanges;
        a.last=pos; a.hadPosition=true;
        Vec relative{pos.xyz.x-camera.xyz.x+160.0f*((int)pos.blockX-(int)camera.blockX),
                     pos.xyz.y-camera.xyz.y+160.0f*((int)pos.blockY-(int)camera.blockY),
                     pos.xyz.z-camera.xyz.z};
        float distanceSquared=Dot(relative,relative);
        if(distanceSquared>options.range*options.range || distanceSquared<.25f)continue;
        a.distance=__builtin_sqrtf(distanceSquared);a.nearby=true;
        if(a.id==selectedId){
            bool exists=false;for(unsigned s=0;s<observedCount;++s)if(observedIds[s]==a.id)exists=true;
            if(!exists && observedCount<512)observedIds[observedCount++]=a.id;
        }
        for(unsigned s=0;s<observedCount;++s)if(observedIds[s]==a.id)a.observedSelectable=true;
        Quaternion q=pos.rotation;
        float norm=q.w*q.w+q.x*q.x+q.y*q.y+q.z*q.z;
        if (!Finite(norm) || Abs(norm-1)>.25f) continue;
        Vec lo{-.65f,-.65f,0}, hi{.65f,.65f,1.8f};
        Vec bounds[2];
        if (a.model && ReadLocal(a.model+0xd0,bounds,sizeof(bounds)) && Valid(bounds[0]) && Valid(bounds[1]) &&
            bounds[1].x>bounds[0].x && bounds[1].y>bounds[0].y && bounds[1].z>bounds[0].z &&
            bounds[1].x-bounds[0].x<20 && bounds[1].y-bounds[0].y<20 && bounds[1].z-bounds[0].z<20 &&
            bounds[1].z-bounds[0].z>.1f && Abs(bounds[0].z)<10) { lo=bounds[0]; hi=bounds[1]; }
        float minX=10000000,minY=10000000,maxX=-10000000,maxY=-10000000;
        bool good=true;
        for (unsigned corner=0;corner<8;++corner) {
            Vec v{(corner&1)?hi.x:lo.x,(corner&2)?hi.y:lo.y,(corner&4)?hi.z:lo.z};
            float x=0,y=0;
            if (!Project(Add(relative,Rotate(q,v)),&x,&y)) { good=false; break; }
            minX=Min(minX,x); minY=Min(minY,y); maxX=Max(maxX,x); maxY=Max(maxY,y);
        }
        if (!good || maxX<0 || maxY<0 || minX>bufferWidth || minY>bufferHeight) continue;
        float width=maxX-minX,height=maxY-minY;
        a.onScreen=true;
        if(!Allowed(a.id) || !circlesEnabled)continue;
        float scale=Min(1.0f,(float)options.maxPixels/Max(width,height));
        int halfW=(int)Max(7,width*scale*.5f),halfH=(int)Max(9,height*scale*.5f);
        int radius=(int)Min(24,Max(7,width*.18f));
        if(circlesCount>=512)continue;
        circles[circlesCount++]={(int)((minX+maxX)*.5f),(int)((minY+maxY)*.5f),radius,a.id,relative,halfW,halfH,i};
    }
}

struct Text {
    char data[524288]; size_t n;
    void Put(char c) { if (n+1<sizeof(data)) data[n++]=c; }
    void String(const char* s) { while (*s) Put(*s++); }
    void Number(uint64_t x, unsigned base=10) {
        char rev[32]; unsigned count=0;
        do { unsigned d=(unsigned)(x%base); rev[count++]=(char)(d<10?'0'+d:'a'+d-10); x/=base; } while(x);
        while(count) Put(rev[--count]);
    }
    void Signed(int64_t x) { if(x<0){Put('-');Number((uint64_t)(-x));}else Number((uint64_t)x); }
    void Float(float x) {
        if(!Finite(x)){String("null");return;}
        if(x<0){Put('-');x=-x;}
        unsigned scaled=(unsigned)(x*1000+.5f);
        Number(scaled/1000);Put('.');Put((char)('0'+(scaled/100)%10));Put((char)('0'+(scaled/10)%10));Put((char)('0'+scaled%10));
    }
    void Vector(Vec v) { Put('[');Float(v.x);Put(',');Float(v.y);Put(',');Float(v.z);Put(']'); }
    void Wide(const wchar_t* s) {
        Put('"');
        for(unsigned i=0;s[i] && i<95;++i){
            unsigned c=(unsigned)s[i];
            if(c=='"' || c=='\\'){Put('\\');Put((char)c);}
            else if(c>=32 && c<127)Put((char)c);
            else {String("\\u");for(int shift=12;shift>=0;shift-=4){unsigned d=(c>>shift)&15;Put((char)(d<10?'0'+d:'a'+d-10));}}
        }
        Put('"');
    }
};
static Text report;
static void AppendWide(wchar_t* out, size_t cap, const wchar_t* text) {
    size_t n=0;while(n<cap && out[n])++n;
    while(*text && n+1<cap)out[n++]=*text++;
    if(n<cap)out[n]=0;
}
static void AppendPid(wchar_t* out, size_t cap) {
    wchar_t rev[16];unsigned n=0;DWORD x=GetCurrentProcessId();
    do {rev[n++]=(wchar_t)('0'+x%10);x/=10;}while(x);
    while(n){wchar_t s[2]={rev[--n],0};AppendWide(out,cap,s);}
}
static void InitNames() {
    GetTempPathW(MAX_PATH,outputDir);AppendWide(outputDir,MAX_PATH,L"LotroVisualInterno");
    CreateDirectoryW(outputDir,nullptr);AppendWide(outputDir,MAX_PATH,L"\\pid-");AppendPid(outputDir,MAX_PATH);
    CreateDirectoryW(outputDir,nullptr);
    AppendWide(statusPath,MAX_PATH,outputDir);AppendWide(statusPath,MAX_PATH,L"\\estado.json");
    AppendWide(optionsPath,MAX_PATH,outputDir);AppendWide(optionsPath,MAX_PATH,L"\\opcoes.bin");
    AppendWide(Movement::path,MAX_PATH,outputDir);AppendWide(Movement::path,MAX_PATH,L"\\velocidade.bin");
    AppendWide(eventName,96,L"Local\\CodexLotroVisualStop-");AppendPid(eventName,96);
    AppendWide(enabledName,96,L"Local\\CodexLotroVisualEnabled-");AppendPid(enabledName,96);
    AppendWide(readyName,96,L"Local\\CodexLotroVisualReady-");AppendPid(readyName,96);
    AppendWide(className,96,L"CodexLotroVisual-");AppendPid(className,96);
}
static void SaveStatus(const char* state) {
    report.n=0;report.String("{\n\"estado\":\"");report.String(state);report.String("\",\n\"pid\":");report.Number(GetCurrentProcessId());
    report.String(",\n\"protocolo\":3,\n\"revisao_visual\":4,\n\"inicio_processo\":\"");report.Number(processCreationTime);report.Put('"');
    report.String(",\n\"movimento\":{\"disponivel\":");report.String(Movement::available?"true":"false");
    report.String(",\"ativo\":");report.String(Movement::applied?"true":"false");
    report.String(",\"multiplicador_atual\":");if(Movement::available)report.Float(Movement::current);else report.String("null");
    report.String(",\"original\":");report.Float(Movement::original);
    report.String(",\"solicitacao\":");report.Number(Movement::serial);
    report.String(",\"gravacoes\":");report.Number(Movement::writes);
    report.String(",\"velocidade_observada\":");report.Float(Movement::measured);
    report.String(",\"resultado\":\"");report.String(Movement::result);report.String("\"}");
    report.String(",\n\"opcoes_revisao\":");report.Number(options.revision);
    report.String(",\n\"estilo\":");report.Number(options.style);
    report.String(",\n\"lista\":[");
    unsigned rows=0;
    for(unsigned i=0;i<actorsCount;++i){
        Actor& a=actors[i];if(!a.nearby)continue;
        if(rows++)report.Put(',');
        report.String("{\"id\":\"0x");report.Number(a.id,16);report.String("\",\"nome\":");report.Wide(a.name);
        report.String(",\"chave_nome\":\"0x");report.Number(a.nameKey,16);report.Put('"');
        report.String(",\"tipo\":\"");report.String(EntityKind(a.weenieType,a.isPlayer));report.Put('"');
        report.String(",\"tipo_codigo\":");if(a.typeKnown)report.Number(a.weenieType);else report.String("null");
        report.String(",\"eh_jogador\":");report.String(a.isPlayer?"true":"false");
        report.String(",\"fonte_nome\":\"");report.String(a.nameSource==1?"memoria":a.nameSource==2?"recurso_local":"pendente");report.Put('"');
        report.String(",\"distancia\":");report.Float(a.distance);
        report.String(",\"na_tela\":");report.String(a.onScreen?"true":"false");
        report.String(",\"selecionavel\":");report.String(a.observedSelectable?"true":"null");
        report.String(",\"atacavel\":null,\"selecionado\":");report.String(a.id==selectedId?"true":"false");report.Put('}');
    }
    report.Put(']');
    report.String(",\n\"nomes_consultas\":");report.Number(LocalNames::lookups);
    report.String(",\n\"nomes_cache\":");report.Number(LocalNames::hits);
    report.String(",\n\"nomes_pendentes_consultas\":");report.Number(LocalNames::misses);
    report.String(",\n\"limite_entidades\":512,\n\"limite_atingido\":");report.String(actorsCount==512?"true":"false");
    report.String(",\n\"circulos_ligados\":");report.String(circlesEnabled?"true":"false");
    report.String(",\n\"leitura\":\"DLL interna; GetCurrentProcess\",\n\"independe_de_selecao\":true");
    report.String(",\n\"janela_overlay\":");report.Number((uintptr_t)overlayWindow);
    report.String(",\n\"janela_jogo\":");report.Number((uintptr_t)gameWindow);
    report.String(",\n\"visivel\":");report.String(displayed?"true":"false");
    report.String(",\n\"camera_valida\":");report.String(cameraValid?"true":"false");
    report.String(",\n\"quadros_atualizados\":");report.Number(frames);
    report.String(",\n\"quadros_desenhados\":");report.Number(paintedFrames);
    report.String(",\n\"entidades\":");report.Number(actorsCount);
    report.String(",\n\"circulos\":");report.Number(circlesCount);
    report.String(",\n\"mudancas_posicao\":");report.Number(positionChanges);
    report.String(",\n\"mudancas_camera\":");report.Number(cameraChanges);
    report.String(",\n\"falhas_copia\":");report.Number(readFailures);
    report.String(",\n\"erro_windows\":");report.Number(lastErrorCode);
    report.String(",\n\"tamanho_tela\":[");report.Signed(bufferWidth);report.Put(',');report.Signed(bufferHeight);report.Put(']');
    report.String(",\n\"jogador_id\":\"0x");report.Number(localPlayerId,16);report.Put('"');
    report.String(",\n\"alvo_selecionado\":\"0x");report.Number(selectedId,16);report.Put('"');
    report.String(",\n\"camera\":{\"posicao\":");report.Vector(camera.xyz);
    report.String(",\"right\":");report.Vector(camera.right);report.String(",\"forward\":");report.Vector(camera.forward);
    report.String(",\"up\":");report.Vector(camera.up);report.String(",\"fov\":");report.Float(camera.fov);
    report.String(",\"bloco\":[");report.Number(camera.blockX);report.Put(',');report.Number(camera.blockY);report.String("]}");
    report.String(",\n\"marcadores\":[");
    for(unsigned i=0;i<circlesCount;++i){
        if(i)report.Put(',');Circle c=circles[i];report.String("{\"id\":\"0x");report.Number(c.id,16);
        report.String("\",\"x\":");report.Signed(c.x);report.String(",\"y\":");report.Signed(c.y);
        report.String(",\"raio\":");report.Signed(c.r);report.String(",\"relativa\":");report.Vector(c.relative);report.Put('}');
    }
    report.String("]\n}\n");
    wchar_t tmp[MAX_PATH]{};AppendWide(tmp,MAX_PATH,outputDir);AppendWide(tmp,MAX_PATH,L"\\estado.tmp");
    HANDLE file=CreateFileW(tmp,GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file==INVALID_HANDLE_VALUE)return;
    DWORD done=0;bool ok=WriteFile(file,report.data,(DWORD)report.n,&done,nullptr) && done==report.n;
    CloseHandle(file);if(ok)MoveFileExW(tmp,statusPath,MOVEFILE_REPLACE_EXISTING);
}

static BOOL CALLBACK FindGame(HWND window, LPARAM) {
    DWORD pid=0;pGetWindowThreadProcessId(window,&pid);
    RECT r{};
    if(pid==GetCurrentProcessId() && window!=overlayWindow && !pGetWindow(window,GW_OWNER) &&
       pIsWindowVisible(window) && pGetClientRect(window,&r) && r.right>=320 && r.bottom>=200) {
        gameWindow=window;return FALSE;
    }
    return TRUE;
}
static bool ResizeBuffer(int width,int height) {
    if(width==bufferWidth && height==bufferHeight && bufferBitmap)return true;
    HDC dc=pGetDC(gameWindow);if(!dc)return false;
    HBITMAP bitmap=pCreateCompatibleBitmap(dc,width,height);pReleaseDC(gameWindow,dc);
    if(!bitmap)return false;
    HBITMAP old=(HBITMAP)pSelectObject(bufferDc,bitmap);
    if(bufferBitmap)pDeleteObject(bufferBitmap);else oldBitmap=old;
    bufferBitmap=bitmap;bufferWidth=width;bufferHeight=height;return true;
}
static void DrawBuffer() {
    RECT r{0,0,bufferWidth,bufferHeight};pFillRect(bufferDc,&r,blackBrush);
    HGDIOBJ priorPen=pSelectObject(bufferDc,greenPen);
    HGDIOBJ priorBrush=pSelectObject(bufferDc,pGetStockObject(NULL_BRUSH));
    for(unsigned i=0;i<circlesCount;++i){
        Circle c=circles[i];
        for(unsigned pass=0;pass<2;++pass){
            pSelectObject(bufferDc,pass?(c.id==selectedId?selectedPen:greenPen):shadowPen);
            if(options.style==1)pEllipse(bufferDc,c.x-c.r,c.y-c.r,c.x+c.r+1,c.y+c.r+1);
            else if(options.style==2){
                int y=c.y-c.halfH-8;
                pMoveToEx(bufferDc,c.x-5,y-4,nullptr);pLineTo(bufferDc,c.x,y+1);pLineTo(bufferDc,c.x+5,y-4);
            } else {
                int length=(int)Min(12,Min((float)c.halfW,(float)c.halfH)*.6f);
                for(int sx=-1;sx<=1;sx+=2)for(int sy=-1;sy<=1;sy+=2){
                    int x=c.x+sx*c.halfW,y=c.y+sy*c.halfH;
                    pMoveToEx(bufferDc,x-sx*length,y,nullptr);pLineTo(bufferDc,x,y);pLineTo(bufferDc,x,y-sy*length);
                }
            }
        }
        const wchar_t* displayName=actors[c.actorIndex].name;
        if(options.labels && displayName[0]){
            const wchar_t* name=displayName;int n=0;while(name[n] && n<95)++n;
            pSetBkMode(bufferDc,TRANSPARENT);pSetTextColor(bufferDc,RGB(230,237,231));
            pTextOutW(bufferDc,c.x-c.halfW,c.y-c.halfH-18,name,n);
        }
    }
    pSelectObject(bufferDc,priorBrush);pSelectObject(bufferDc,priorPen);
}
static LRESULT CALLBACK WindowProc(HWND window,UINT message,WPARAM w,LPARAM l) {
    if(message==WM_NCHITTEST)return HTTRANSPARENT;
    if(message==WM_MOUSEACTIVATE)return MA_NOACTIVATE;
    if(message==WM_ERASEBKGND)return 1;
    if(message==WM_PAINT){
        PAINTSTRUCT p{};HDC dc=pBeginPaint(window,&p);
        if(dc && bufferBitmap){pBitBlt(dc,0,0,bufferWidth,bufferHeight,bufferDc,0,0,SRCCOPY);++paintedFrames;}
        pEndPaint(window,&p);return 0;
    }
    if(message==WM_CLOSE){SetEvent(stopEvent);return 0;}
    return pDefWindowProcW(window,message,w,l);
}

static bool SupportedBuild() {
    moduleBase=(uintptr_t)GetModuleHandleW(nullptr);
    if(ReadValue<uint16_t>(moduleBase)!=0x5a4d)return false;
    unsigned pe=ReadValue<unsigned>(moduleBase+0x3c);
    if(pe>0x100000 || ReadValue<unsigned>(moduleBase+pe)!=0x4550)return false;
    return ReadValue<unsigned>(moduleBase+pe+8)==0x6a947bf8 &&
           ReadValue<unsigned>(moduleBase+pe+24+56)==0x22bd000;
}

static void CloseControlHandles() {
    if(readyEvent){ResetEvent(readyEvent);CloseHandle(readyEvent);readyEvent=nullptr;}
    if(enabledEvent){CloseHandle(enabledEvent);enabledEvent=nullptr;}
    if(stopEvent){CloseHandle(stopEvent);stopEvent=nullptr;}
    if(singleInstance){CloseHandle(singleInstance);singleInstance=nullptr;}
}

static bool CreateControlEvents() {
    stopEvent=CreateEventW(nullptr,TRUE,FALSE,eventName);
    if(!stopEvent){lastErrorCode=GetLastError();return false;}
    enabledEvent=CreateEventW(nullptr,TRUE,TRUE,enabledName);
    if(!enabledEvent){lastErrorCode=GetLastError();return false;}
    readyEvent=CreateEventW(nullptr,TRUE,FALSE,readyName);
    if(!readyEvent){lastErrorCode=GetLastError();return false;}
    // Handles abertos por um controlador podem sobreviver ao worker anterior.
    if(!ResetEvent(stopEvent) || !ResetEvent(readyEvent) || !SetEvent(enabledEvent)){
        lastErrorCode=GetLastError();return false;
    }
    return true;
}

static bool CreateDrawingResources() {
    overlayWindow=pCreateWindowExW(WS_EX_LAYERED|WS_EX_TRANSPARENT|WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE,
        className,L"LOTRO - circulos verdes (DLL interna)",WS_POPUP,0,0,1,1,gameWindow,nullptr,imageInstance,nullptr);
    if(!overlayWindow){lastErrorCode=GetLastError();return false;}
    bufferDc=pCreateCompatibleDC(nullptr);
    if(!bufferDc){lastErrorCode=GetLastError();return false;}
    greenPen=pCreatePen(PS_SOLID,1,RGB(111,219,176));
    if(!greenPen){lastErrorCode=GetLastError();return false;}
    shadowPen=pCreatePen(PS_SOLID,3,RGB(18,29,25));
    selectedPen=pCreatePen(PS_SOLID,2,RGB(232,199,115));
    if(!shadowPen || !selectedPen){lastErrorCode=GetLastError();return false;}
    blackBrush=pCreateSolidBrush(RGB(0,0,0));
    if(!blackBrush){lastErrorCode=GetLastError();return false;}
    if(!pSetLayeredWindowAttributes(overlayWindow,RGB(0,0,0),255,LWA_COLORKEY)){
        lastErrorCode=GetLastError();return false;
    }
    return true;
}

static DWORD WINAPI VisualWorker(void*) {
    wchar_t mutexName[96]{};AppendWide(mutexName,96,L"Local\\CodexLotroVisual-");AppendPid(mutexName,96);
    singleInstance=CreateMutexW(nullptr,FALSE,mutexName);
    DWORD mutexError=GetLastError();
    // Uma segunda imagem nao deve sobrescrever o estado do worker existente.
    if(singleInstance && mutexError==ERROR_ALREADY_EXISTS){CloseHandle(singleInstance);singleInstance=nullptr;return ERROR_ALREADY_EXISTS;}
    InitNames();
    if(!singleInstance){lastErrorCode=mutexError;circlesEnabled=false;SaveStatus("falha_mutex");return lastErrorCode;}
    FILETIME created{},exited{},kernel{},user{};
    if(!GetProcessTimes(GetCurrentProcess(),&created,&exited,&kernel,&user)){
        lastErrorCode=GetLastError();circlesEnabled=false;SaveStatus("falha_identidade");CloseControlHandles();return lastErrorCode;
    }
    processCreationTime=((uint64_t)created.dwHighDateTime<<32)|created.dwLowDateTime;
    options.style=0;options.maxPixels=100;options.range=80;
    SaveStatus("iniciando");
    if(!SupportedBuild()){lastErrorCode=ERROR_BAD_EXE_FORMAT;circlesEnabled=false;SaveStatus("versao_nao_suportada");CloseControlHandles();return lastErrorCode;}
    if(!CreateControlEvents()){circlesEnabled=false;SaveStatus("falha_eventos");CloseControlHandles();return lastErrorCode;}
    if(!LoadDrawingApis()){lastErrorCode=GetLastError();circlesEnabled=false;SaveStatus("falha_apis");CloseControlHandles();return lastErrorCode;}
    pEnumWindows(FindGame,0);
    if(!gameWindow){lastErrorCode=ERROR_NOT_FOUND;circlesEnabled=false;SaveStatus("janela_nao_encontrada");CloseControlHandles();return lastErrorCode;}
    // A janela nasce na thread da DLL dentro do PID do LOTRO, sem capturar entrada.
    WNDCLASSEXW wc{};wc.cbSize=sizeof(wc);wc.lpfnWndProc=WindowProc;wc.hInstance=imageInstance;wc.lpszClassName=className;
    ATOM atom=pRegisterClassExW(&wc);
    if(!atom){lastErrorCode=GetLastError();circlesEnabled=false;SaveStatus("falha_classe");CloseControlHandles();return lastErrorCode;}
    bool initialized=CreateDrawingResources();
    const char* terminalState=initialized?"encerrado":"falha_desenho";
    if(initialized && !SetEvent(readyEvent)){
        lastErrorCode=GetLastError();initialized=false;terminalState="falha_eventos";
    }
    DWORD lastEnumeration=0,lastReport=0,lastOptions=0,lastMovement=0;
    int lastX=-100000,lastY=-100000,lastW=0,lastH=0;
    while(initialized && WaitForSingleObject(stopEvent,0)==WAIT_TIMEOUT && pIsWindow(gameWindow)) {
        DWORD now=GetTickCount();
        if(!lastOptions || now-lastOptions>=150){ReadOptions();lastOptions=now;}
        MSG msg{};while(pPeekMessageW(&msg,nullptr,0,0,PM_REMOVE)){pTranslateMessage(&msg);pDispatchMessageW(&msg);}
        DWORD enabledState=WaitForSingleObject(enabledEvent,0);
        if(enabledState==WAIT_FAILED){lastErrorCode=GetLastError();terminalState="falha_eventos";break;}
        circlesEnabled=enabledState==WAIT_OBJECT_0;
        RECT r{};POINT origin{};
        bool menuOpen=options.menuWindow && pIsWindow((HWND)options.menuWindow) && pIsWindowVisible((HWND)options.menuWindow);
        bool collecting=circlesEnabled || menuOpen;
        bool geometry=collecting && pGetClientRect(gameWindow,&r) && pClientToScreen(gameWindow,&origin) && r.right>=320 && r.bottom>=200 && r.right<=10000 && r.bottom<=10000;
        if(geometry && ResizeBuffer(r.right,r.bottom)) {
            if(!lastEnumeration || now-lastEnumeration>=1000){EnumerateActors();ReadSelectionForReport();lastEnumeration=now;}
            UpdateCircles();++frames;DrawBuffer();
            HWND foreground=pGetForegroundWindow();
            bool show=circlesEnabled && pIsWindowVisible(gameWindow) && !pIsIconic(gameWindow) && (foreground==gameWindow || (menuOpen && foreground==(HWND)options.menuWindow));
            if(show && (!displayed || origin.x!=lastX || origin.y!=lastY || r.right!=lastW || r.bottom!=lastH)) {
                pSetWindowPos(overlayWindow,HWND_TOPMOST,origin.x,origin.y,r.right,r.bottom,SWP_NOACTIVATE|SWP_SHOWWINDOW);
                lastX=origin.x;lastY=origin.y;lastW=r.right;lastH=r.bottom;
            } else if(!show && displayed)pShowWindow(overlayWindow,SW_HIDE);
            displayed=show;
            pInvalidateRect(overlayWindow,nullptr,FALSE);
        } else {
            if(displayed)pShowWindow(overlayWindow,SW_HIDE);
            displayed=false;circlesCount=0;cameraValid=false;
            for(unsigned i=0;i<actorsCount;++i){actors[i].nearby=false;actors[i].onScreen=false;}
            if(!collecting){
                actorsCount=0;cameraValid=false;localPlayerId=0;selectedId=0;lastEnumeration=0;
            }
        }
        if(!lastMovement || now-lastMovement>=200){Movement::Tick(ReadValue<uint64_t>(moduleBase+0x1e42748),processCreationTime);lastMovement=now;}
        if(!lastReport || now-lastReport>=1000){
            if(circlesEnabled)ReadSelectionForReport();
            SaveStatus(circlesEnabled?"ativo":"pausado");lastReport=now;
        }
        WaitForSingleObject(stopEvent,30);
    }
    Movement::Restore("restaurado_worker_encerrado");
    ResetEvent(readyEvent);displayed=false;circlesEnabled=false;circlesCount=0;actorsCount=0;cameraValid=false;
    if(overlayWindow){pDestroyWindow(overlayWindow);overlayWindow=nullptr;}
    if(bufferBitmap){pSelectObject(bufferDc,oldBitmap);pDeleteObject(bufferBitmap);}
    if(bufferDc)pDeleteDC(bufferDc);
    if(greenPen)pDeleteObject(greenPen);
    if(shadowPen)pDeleteObject(shadowPen);
    if(selectedPen)pDeleteObject(selectedPen);
    if(blackBrush)pDeleteObject(blackBrush);
    pUnregisterClassW(className,imageInstance);
    LocalNames::Close();
    SaveStatus(terminalState);
    CloseControlHandles();
    return lastErrorCode;
}

static LPTHREAD_START_ROUTINE volatile workerAddress=VisualWorker;
extern "C" BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID) {
    if(reason==DLL_PROCESS_ATTACH){
        imageInstance=instance;
        HANDLE thread=CreateThread(nullptr,0,workerAddress,nullptr,0,nullptr);
        if(!thread)return FALSE;CloseHandle(thread);
    }
    return TRUE;
}
