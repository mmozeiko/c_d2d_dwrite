# Direct2D and DirectWrite C headers

Headers are generated from [Win32 Metadata][] .winmd file (included in repo).

Headers have minor changes to make them to compile as C code - whenever there is overloaded method with same
name, it has incrementing number appended to name.

For example, interface [ID2D1DeviceContext][] has [CreateBitmap][ID2D1DeviceContext-CreateBitmap] method, but
its parent interface [ID2D1RenderTarget][] also has [CreateBitmap][ID2D1RenderTarget-CreateBitmap] method - with
different arguments. So whichever method comes last is renamed to `CreateBitmap1`. In this case it will be
`ID2D1DeviceContext_CreateBitmap1` method.

Some methods have more overloads, so to use them you will need to append suffix with 2, 3, 4, 5 or larger number.

Generated headers have proper workaround for wrong return types violating COM ABI. Read
[Direct 2D Scene of the Accident][d2d-accident] for more information.

# Running generator

Run `dotnet run --project Generator`. It will write output `cd2d.h` and `cdwrite.h` files.

[Win32 Metadata]: https://www.nuget.org/packages/Microsoft.Windows.SDK.Win32Metadata/
[ID2D1DeviceContext]: https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/nn-d2d1_1-id2d1devicecontext
[ID2D1RenderTarget]: https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1rendertarget
[ID2D1DeviceContext-CreateBitmap]: https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-createbitmap(d2d1_size_u_constd2d1_bitmap_properties__id2d1bitmap)
[ID2D1RenderTarget-CreateBitmap]: https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-createbitmap(d2d1_size_u_constvoid_uint32_constd2d1_bitmap_properties__id2d1bitmap)
[d2d-accident]: https://blog.airesoft.co.uk/2014/12/direct2d-scene-of-the-accident/
