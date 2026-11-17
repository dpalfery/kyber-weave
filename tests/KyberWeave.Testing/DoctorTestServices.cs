using KyberWeave.Application;
using KyberWeave.Application.Doctor;
using Microsoft.Extensions.DependencyInjection;

namespace KyberWeave.Tests;

/// <summary>The doctor services as the application registers them, for tests that drive them directly.</summary>
internal static class DoctorTestServices
{
    private static readonly ServiceProvider Provider = new ServiceCollection().AddWeaveApplication().BuildServiceProvider();

    /// <summary>Installs and inspects the git post-checkout hook.</summary>
    public static PostCheckoutHookInstaller PostCheckout { get; } = Provider.GetRequiredService<PostCheckoutHookInstaller>();
}
