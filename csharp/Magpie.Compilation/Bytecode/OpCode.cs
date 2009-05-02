﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Magpie.Compilation
{
    /// <summary>
    /// The byte codes.
    /// </summary>
    /// <remarks>There is a mirror of this file in the compiler. These two must be kept in sync for obvious
    /// reasons. They are duplicated to make it explicit that there is little real dependency between the
    /// compiler and the interpreter.</remarks>
    public enum OpCode : byte
    {
        // pushes the subsequent constant onto the stack
        PushNull,
        PushBool,
        PushInt,
        PushString, // operand is an integer index into the string table

        PushLocals,

        // allocates a structure on the heap, initializes each slot by popping values,
        // then pushes a reference to it
        Alloc, // int : number of slots

        // pops a reference to a structure, and pushes the value of the field at the given index
        Load, // byte : index of field

        // pops a value and a reference to a structure, and assigns the value to the field at the given index
        Store, // byte : index of field

        //### bob: function index is temp. should eventually be byte offset to start of fn
        Call0,        // pops function index from operand stack
        Call1,        // pops function index from operand stack
        CallN,        // pops function index from operand stack

        ForeignCall0, // int : foreign function identifier
        ForeignCall1, // int : foreign function identifier
        ForeignCallN, // int : foreign function identifier

        Return,

        Jump,
        JumpIfFalse, // int: new instruction offset

        // conversion
        BoolToString,
        IntToString,

        // comparison
        EqualBool,
        EqualInt,
        EqualString,

        LessInt,
        GreaterInt,

        // arithmetic
        NegateBool,
        NegateInt,

        AndBool,
        OrBool,

        AddInt,
        SubInt,
        MultInt,
        DivInt,

        // optional
        HasValue,
        BoxValue,
        UnboxValue,

        // strings
        AddString,
        Print,   //### bob: temp until there's some sort of platform interface
        StringSize,
        Substring
    }
}
