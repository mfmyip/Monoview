using WixToolset.BootstrapperApplicationApi;

namespace Monoview.Setup;

internal static class Program
{
    private static int Main()
    {
        ManagedBootstrapperApplication.Run(new SetupApp());
        return 0;
    }
}
