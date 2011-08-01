#pragma once

#include "Macros.h"
#include "Memory.h"

namespace magpie {
  class VM;
  
  // Base class for any object that is memory managed by the garbage collector
  class Managed {
  public:
    Managed() {}
    
    virtual ~Managed() {}
    
    // Gets the forwarding pointer that this object has been replaced with if
    // it has been. Otherwise returns NULL.
    virtual Managed* getForwardingAddress() const { return NULL; }
    
    // Subclasses must override this to provide their size in bytes so that the
    // copying collector can copy them.
    virtual size_t getSize() const = 0;
    
    // This will be called by the garbage collector when this object has been
    // reached. Subclasses should override this and call Memory::copy() on any
    // gc<T> references that the object contains.
    virtual void reachRefs(Memory& memory) {}

    void* operator new(size_t s, VM& vm);
    
  private:
    
    NO_COPY(Managed);
  };

}