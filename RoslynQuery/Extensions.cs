using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace System
{
    public readonly struct Index(int v, bool fromEnd)
    {
        public readonly int GetOffset(int length) => fromEnd ? length - v : v;
        public static implicit operator Index(int value) => new Index(value, false);
    }
    public readonly struct Range(Index start, Index end)
    {
        public Index Start => start;
        public Index End => end;

        public readonly (int Offset, int Length) GetOffsetAndLength(int length)
        {
            var startOffset = start.GetOffset(length);
            var endOffset = end.GetOffset(length);
            if (startOffset > endOffset)
                throw new ArgumentOutOfRangeException(nameof(start), "Start index must be less than or equal to end index.");
            return (startOffset, endOffset - startOffset);
        }
    }
}

namespace RoslynQuery
{
    internal static class Extensions
    {
        private struct VoidStruct { }
        extension(ValueTask)
        {
            public static ValueTask FromCanceled(CancellationToken cancellationToken)
            {
                var tcs = new TaskCompletionSource<VoidStruct>();
                tcs.TrySetCanceled(cancellationToken);
                return new ValueTask(tcs.Task);
            }
            public static ValueTask<T> FromCanceled<T>(CancellationToken cancellationToken)
            {
                var tcs = new TaskCompletionSource<T>();
                tcs.TrySetCanceled(cancellationToken);
                return new ValueTask<T>(tcs.Task);
            }
        }
        extension(ObjectDisposedException)
        {
            public static void ThrowIf(bool condition, object instance) => ThrowIf(condition, instance?.GetType());
            public static void ThrowIf(bool condition, Type type)
            {
                if (condition)
                    throw new ObjectDisposedException(type?.FullName);
            }
        }
        extension(ArgumentException)
        {
            public static void ThrowIfNull(object argument, string paramName = null)
            {
                if (argument == null)
                    throw new ArgumentNullException(paramName);
            }
        }
        extension(Array)
        {
            public static int MaxLength => 0x7FFFFFC7;
        }
        extension<T>(List<T> list)
        {
            public ListMirror<T> Mirror() => ListMirror<T>.From(list);
        }
        extension<T>(in Span<T> span)
        {
            [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
            public void ZeroMemory() => span.Clear();
        }
    }

    internal class ListMirror<T>
    {
        private ListMirror() { }
        public static ListMirror<T> From(List<T> list) => Unsafe.As<ListMirror<T>>(list);

        public T[] _items;
        public int _size;
        public int _version;
        public object _syncRoot;
    }
}