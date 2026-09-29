#pragma once
// Bounded experiment on the local player's private float property 0x241.
// Never edits a shared provider, executable code, other entities or the server.
// The client reads this float in RVA 72b600 and multiplies directional speeds.
namespace Movement {
#pragma pack(push,1)
struct Request { uint32_t magic,version;uint64_t creation,expires;uint32_t serial;float multiplier; };
#pragma pack(pop)
static_assert(sizeof(Request)==32,"Movement request size");
static wchar_t path[MAX_PATH];
static uintptr_t entity,activeEntity,activeSlot;
static uint64_t id,activeId,expires;
static uint32_t serial;
static bool applied,available;
static float original,current,changed,measured;
static unsigned writes;
static const char* result="sem_teste";
static float lastX,lastY,lastZ;
static uint16_t lastRegion;
static DWORD lastPositionTime;
static uint64_t lastPositionId;
static bool ValidFloat(float v){return v==v && v>-10000000.f && v<10000000.f;}
static uint64_t Now(){FILETIME f{};GetSystemTimeAsFileTime(&f);return ((uint64_t)f.dwHighDateTime<<32)|f.dwLowDateTime;}
static bool Slot(uintptr_t owner,uint64_t ownerId,uintptr_t* slot,float* value){
    if(!owner || !ownerId || ReadValue<uint64_t>(owner+0x118)!=ownerId)return false;
    uintptr_t component=ReadValue<uintptr_t>(owner+0xc0);
    uintptr_t table=ReadValue<uintptr_t>(component+0x48);unsigned count=ReadValue<unsigned>(component+0x58);
    if(!component || !table || !count || count>65536)return false;
    uintptr_t node=ReadValue<uintptr_t>(table+(0x241%count)*8);
    for(unsigned i=0;node && i<128;++i){
        unsigned char data[32]{};if(!ReadLocal(node,data,sizeof(data)))return false;
        if(Field<uint32_t>(data,0)==0x241){
            uintptr_t descriptor=Field<uintptr_t>(data,16);
            if(!descriptor || ReadValue<unsigned>(descriptor+0x14)!=9)return false;
            float v=Field<float>(data,24);if(!ValidFloat(v) || v<0.1f || v>8.f)return false;
            *slot=node+24;*value=v;return true;
        }
        uintptr_t next=Field<uintptr_t>(data,8);if(next==node)return false;node=next;
    }
    return false;
}
static bool Write(uintptr_t owner,uint64_t ownerId,uintptr_t expectedSlot,float expected,float next){
    uintptr_t slot=0;float value=0;
    if(!Slot(owner,ownerId,&slot,&value) || slot!=expectedSlot || value!=expected)return false;
    SIZE_T done=0;
    if(!WriteProcessMemory(GetCurrentProcess(),(void*)slot,&next,sizeof(next),&done) || done!=sizeof(next))return false;
    ++writes;return ReadValue<float>(slot)==next;
}
static void Restore(const char* why){
    if(!applied)return;
    bool ok=Write(activeEntity,activeId,activeSlot,changed,original);
    applied=false;result=ok?why:"valor_mudou_ou_entidade_indisponivel";
}
static bool ValidRequest(const Request& r,uint64_t creation,uint64_t now){
    return r.magic==0x53564c43 && r.version==1 && r.creation==creation && r.serial &&
        r.expires>now && r.expires-now<=UINT64_C(1200000000) && ValidFloat(r.multiplier) && r.multiplier>=1.f && r.multiplier<=2.f;
}
static void Accept(const Request& r,uint64_t creation,uint64_t now){
    if(!ValidRequest(r,creation,now) || r.serial==serial)return;
    Restore("restaurado");serial=r.serial;
    if(r.multiplier==1.f){result="normal";return;}
    uintptr_t slot=0;float value=0;
    if(!Slot(entity,id,&slot,&value)){result="propriedade_privada_indisponivel";return;}
    float next=value*r.multiplier;if(next>8.f){result="fora_do_limite";return;}
    activeEntity=entity;activeId=id;activeSlot=slot;original=value;changed=next;expires=r.expires;
    if(Write(entity,id,slot,value,next)){applied=true;result="aplicado_aguardando_validacao_de_movimento";}
    else result="gravacao_nao_confirmada";
}
static void Measure(){
    DWORD now=GetTickCount();uintptr_t render=ReadValue<uintptr_t>(entity+8);
    unsigned char pos[0x38]{};
    if(!entity || !id || ReadValue<uint64_t>(entity+0x118)!=id || ReadValue<uintptr_t>(render+0x18)!=entity ||
       !ReadLocal(render+0x98,pos,sizeof(pos)) || Field<uint32_t>(pos,8)!=1){lastPositionTime=0;measured=0;return;}
    float x=Field<float>(pos,0x18)+pos[12]*160.f,y=Field<float>(pos,0x1c)+pos[13]*160.f,z=Field<float>(pos,0x20);
    uint16_t region=Field<uint16_t>(pos,14);
    if(!ValidFloat(x)||!ValidFloat(y)||!ValidFloat(z)){lastPositionTime=0;measured=0;return;}
    DWORD elapsed=now-lastPositionTime;
    measured=0;
    if(lastPositionTime && lastPositionId==id && region==lastRegion && elapsed>=100 && elapsed<=3000){
        float dx=x-lastX,dy=y-lastY,dz=z-lastZ;
        float speed=__builtin_sqrtf(dx*dx+dy*dy+dz*dz)*1000.f/elapsed;
        if(ValidFloat(speed) && speed<100.f)measured=speed;
    }
    lastX=x;lastY=y;lastZ=z;lastRegion=region;lastPositionId=id;lastPositionTime=now;
}
static void Tick(uint64_t playerId,uint64_t creation){
    uint64_t now=Now();
    if(applied && (now>=expires || playerId!=activeId || entity!=activeEntity))Restore("restaurado_fim_teste");
    uintptr_t slot=0;available=id==playerId && Slot(entity,id,&slot,&current);
    if(applied && (!available || slot!=activeSlot || current!=changed)){applied=false;result="jogo_alterou_o_valor";}
    HANDLE file=CreateFileW(path,GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file!=INVALID_HANDLE_VALUE){Request r{};DWORD n=0;bool ok=ReadFile(file,&r,sizeof(r),&n,nullptr)!=FALSE;CloseHandle(file);if(ok && n==sizeof(r))Accept(r,creation,now);}
    Measure();
}
}
