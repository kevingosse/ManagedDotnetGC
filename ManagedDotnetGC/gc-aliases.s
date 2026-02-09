/* Symbol aliases for Linux GC exports */
/* These create GC_Initialize as a global alias to _GC_Initialize */
    
    .text
    .globl GC_Initialize
    .type GC_Initialize, @function
GC_Initialize:
    jmp _GC_Initialize
    .size GC_Initialize, .-GC_Initialize

    .globl GC_VersionInfo
    .type GC_VersionInfo, @function
GC_VersionInfo:
    jmp _GC_VersionInfo
    .size GC_VersionInfo, .-GC_VersionInfo
    
    /* Mark stack as non-executable */
    .section .note.GNU-stack,"",@progbits
