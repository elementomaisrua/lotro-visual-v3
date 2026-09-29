#pragma once
// WeenieType, property 0x126: enums 0x230000c9 and 0x230001ab in
// client_general.dat. Names and hostility are not classification rules.
static const char* EntityKind(uint32_t type,bool player) {
    if(player || type==0x1f || type==0x3f)return "jogador";
    if(type==0x1004f)return "monstro";
    if(type==0x2004f)return "realnpc";
    if(type==0x4f || type==0x4004f)return "npc";
    if((type&0xffff)==0x81 || (type&0xffff)==3 || (type&0xffff)==0x103)return "objeto";
    switch(type){
        case 0x400001: case 0x800001: case 0x1000001: case 0x30001:
        case 0x100001: case 0x500001: case 0x60001: case 0xd00001:
        case 0x200001: return "objeto";
        default: return "nao_classificado";
    }
}
