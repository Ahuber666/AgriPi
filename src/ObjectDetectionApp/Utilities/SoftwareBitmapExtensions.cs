using System;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace ObjectDetectionApp.Utilities;

public static class SoftwareBitmapExtensions
{
    public static byte[] ToBgra8Bytes(this SoftwareBitmap bitmap)
    {
        if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || bitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
        {
            using var converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            return ExtractBytes(converted);
        }

        return ExtractBytes(bitmap);
    }

    public static WriteableBitmap ToWriteableBitmap(this SoftwareBitmap bitmap)
    {
        if (bitmap is null)
        {
            throw new ArgumentNullException(nameof(bitmap));
        }

        if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || bitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
        {
            using var converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            return CreateWriteableBitmap(converted);
        }

        return CreateWriteableBitmap(bitmap);
    }

    private static WriteableBitmap CreateWriteableBitmap(SoftwareBitmap bitmap)
    {
        var pixels = ExtractBytes(bitmap);
        var writeableBitmap = new WriteableBitmap(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        writeableBitmap.WritePixels(new System.Windows.Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight), pixels, bitmap.PixelWidth * 4, 0);
        return writeableBitmap;
    }

    private static byte[] ExtractBytes(SoftwareBitmap bitmap)
    {
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        return GetBytes(reference, bitmap.PixelWidth * bitmap.PixelHeight * 4);
    }

    private static unsafe byte[] GetBytes(IMemoryBufferReference reference, int length)
    {
        if (reference is null)
        {
            throw new ArgumentNullException(nameof(reference));
        }

        if (reference is not IMemoryBufferByteAccess byteAccess)
        {
            throw new InvalidCastException("Unable to access memory buffer.");
        }

        byteAccess.GetBuffer(out byte* dataInBytes, out uint capacity);
        var bytes = new byte[Math.Min(length, (int)capacity)];
        Marshal.Copy(new IntPtr(dataInBytes), bytes, 0, bytes.Length);
        return bytes;
    }

    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        unsafe void GetBuffer(out byte* buffer, out uint capacity);
    }
}
