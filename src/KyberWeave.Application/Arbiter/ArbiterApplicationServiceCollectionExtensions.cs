using KyberWeave.Contracts.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KyberWeave.Application.Arbiter;

/// <summary>Registers the Arbiter's use cases.</summary>
internal static class ArbiterApplicationServiceCollectionExtensions
{
    /// <summary>Registers the Arbiter services with <c>TryAdd</c>, so a host or test that registered its own first keeps it.</summary>
    /// <param name="services">The collection to populate.</param>
    /// <returns>The same collection, for chaining.</returns>
    internal static IServiceCollection AddWeaveArbiterApplication(this IServiceCollection services)
    {
        services.TryAddSingleton<IArbiterEvaluatorFactory, Core.Arbiter.ArbiterEvaluatorFactory>();
        services.TryAddSingleton<ICommentTriageFactory, Core.Arbiter.CommentTriageFactory>();
        return services;
    }
}
