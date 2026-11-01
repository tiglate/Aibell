using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Aibell.Platform.Windows.Native;
using NUnit.Framework;

namespace Aibell.Platform.Windows.Tests;

/// <summary>Drives the native COM event handler through its vtable exactly as the WinRT runtime does.</summary>
[TestFixture]
internal sealed unsafe class ButtonPressedHandlerTests
{
    private const int ENoInterface = unchecked((int)0x80004002);
    private static readonly Guid UnknownIid = new("00000000-0000-0000-c000-000000000046");
    private static readonly Guid MarshalIid = new("00000003-0000-0000-c000-000000000046");

    private struct FakeArgs
    {
        public nint* VTable;
        public int Button;
        public int Result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetButton(FakeArgs* self, int* value)
    {
        *value = self->Button;
        return self->Result;
    }

    private static FakeArgs* NewArgs(int button, int result = 0)
    {
        nint* table = (nint*)NativeMemory.AllocZeroed((nuint)(7 * sizeof(nint)));
        table[SmtcSlots.ButtonPressedArgsGetButton] = (nint)(delegate* unmanaged[Stdcall]<FakeArgs*, int*, int>)&GetButton;
        FakeArgs* args = (FakeArgs*)NativeMemory.AllocZeroed((nuint)sizeof(FakeArgs));
        args->VTable = table;
        args->Button = button;
        args->Result = result;
        return args;
    }

    private static void Free(FakeArgs* args)
    {
        NativeMemory.Free(args->VTable);
        NativeMemory.Free(args);
    }

    private static int Invoke(nint handler, FakeArgs* args) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)VTable.Slot(handler, 3))(handler, 0, (nint)args);

    private static int QueryInterface(nint handler, Guid iid, out nint result)
    {
        nint value = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)VTable.Slot(handler, 0))(handler, &iid, &value);
        result = value;
        return hr;
    }

    [Test]
    public void Invoke_ReadsButtonFromArgsAndRaisesCallback()
    {
        List<TransportButton> pressed = [];
        ButtonPressedHandler handler = ButtonPressedHandler.Create(pressed.Add, out nint pointer);
        FakeArgs* args = NewArgs((int)TransportButton.Next);
        try
        {
            Assert.That(Invoke(pointer, args), Is.Zero);
            Assert.That(pressed, Is.EqualTo(new[] { TransportButton.Next }));
        }
        finally { Free(args); handler.Detach(); ButtonPressedHandler.Release(pointer); }
    }

    [Test]
    public void Invoke_FailingArgsOrNullArgs_IsIgnoredWithoutThrowing()
    {
        int calls = 0;
        ButtonPressedHandler handler = ButtonPressedHandler.Create(_ => calls++, out nint pointer);
        FakeArgs* failing = NewArgs(0, unchecked((int)0x80004005));
        try
        {
            Assert.That(Invoke(pointer, failing), Is.Zero);
            Assert.That(Invoke(pointer, null), Is.Zero);
            Assert.That(calls, Is.Zero);
        }
        finally { Free(failing); handler.Detach(); ButtonPressedHandler.Release(pointer); }
    }

    [Test]
    public void Invoke_CallbackThrows_DoesNotEscapeNativeBoundary()
    {
        ButtonPressedHandler handler = ButtonPressedHandler.Create(_ => throw new InvalidOperationException("boom"), out nint pointer);
        FakeArgs* args = NewArgs(0);
        try { Assert.That(Invoke(pointer, args), Is.Zero); }
        finally { Free(args); handler.Detach(); ButtonPressedHandler.Release(pointer); }
    }

    [Test]
    public void Invoke_AfterDetach_DropsLatePresses()
    {
        int calls = 0;
        ButtonPressedHandler handler = ButtonPressedHandler.Create(_ => calls++, out nint pointer);
        FakeArgs* args = NewArgs(0);
        try
        {
            handler.Detach();
            Assert.That(Invoke(pointer, args), Is.Zero);
            Assert.That(calls, Is.Zero);
        }
        finally { Free(args); ButtonPressedHandler.Release(pointer); }
    }

    [Test]
    public void QueryInterface_AnswersHandlerUnknownAndAgileOnly()
    {
        ButtonPressedHandler handler = ButtonPressedHandler.Create(_ => { }, out nint pointer);
        try
        {
            foreach (Guid iid in new[] { UnknownIid, SmtcSlots.ButtonPressedHandlerIid, SmtcSlots.AgileObjectIid })
            {
                Assert.That(QueryInterface(pointer, iid, out nint result), Is.Zero);
                Assert.That(result, Is.EqualTo(pointer));
                Assert.That(ButtonPressedHandler.Release(result), Is.EqualTo(1));
            }
            Assert.That(QueryInterface(pointer, MarshalIid, out nint missing), Is.EqualTo(ENoInterface));
            Assert.That(missing, Is.EqualTo((nint)0));
        }
        finally { handler.Detach(); ButtonPressedHandler.Release(pointer); }
    }

    [Test]
    public void AddRefAndRelease_CountReferencesAndReleaseLastFreesObject()
    {
        ButtonPressedHandler handler = ButtonPressedHandler.Create(_ => { }, out nint pointer);

        Assert.That(ButtonPressedHandler.AddRef(pointer), Is.EqualTo(2));
        Assert.That(ButtonPressedHandler.Release(pointer), Is.EqualTo(1));
        handler.Detach();
        Assert.That(ButtonPressedHandler.Release(pointer), Is.Zero);
    }
}
