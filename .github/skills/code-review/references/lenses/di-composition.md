# Lens: di-composition

## Applicability

Applies when the diff adds or changes a class, or changes how one is constructed or
registered, in a language and codebase using dependency injection.

Skip for changes confined to value types, pure functions, configuration data, or languages
where the project uses no container.

## What this lens owns

Whether collaborators arrive through the constructor or are conjured inside the class. This
is the highest-yield lens in the council on most codebases, because the defect is trivial to
introduce, invisible in a diff read quickly, and expensive to unpick once a call site
depends on it.

## What to look for

**No locally created dependencies.** No class instantiates its own collaborators — not in
constructors, not in methods, not in properties, not in field initializers. Flag every
construction of a service, repository, HTTP client, database context, API client, or similar
injectable type inside a class body.

The acceptable constructions are value objects, data-transfer objects, domain entities,
records, collections, result and option types, exceptions, and other non-injectable data
structures. If the thing being constructed has behaviour that could need substituting in a
test, it is injectable.

**Every collaborator through the constructor.** Services, repositories, clients, loggers,
factories, configuration and options objects, client factories, and clocks or time providers
are injected and stored as fields. A class that reaches out for a collaborator rather than
receiving one has a dependency its callers cannot see and its tests cannot replace.

**Registration actually exists.** For each newly injected dependency, find its registration
in the composition root. An injected type with no registration is a runtime resolution
failure that compiles cleanly and passes any test that does not build the real container —
which is to say it ships.

**Anti-patterns, named.** Service location — resolving from the provider inside a class
rather than declaring the need in the constructor. Static singletons dressed as injected
dependencies. Activation helpers used to hide constructor parameters. Default-constructed
nested services. A parameterless constructor kept "for tests" alongside the real one.

**Lifetime mismatches.** A short-lived dependency captured by a long-lived one — the captured
dependency outlives its intended scope and is silently shared. Read the registrations, not
just the constructors.

**Map every touched class.** For each class in the diff, read the constructor *and* the full
body. The instantiation is rarely in the constructor; it is three methods down, in a helper,
on a line that looks like initialization.

## What this lens must not report

- Construction of data structures, however elaborate.
- Construction inside a factory whose declared job is construction, or inside the composition
  root itself.
- Construction in test code — test doubles and fixtures are built by hand on purpose.
- The number of constructor parameters. A wide constructor is honest about its dependencies;
  it may be a design smell but it is not this defect, and reporting it here dilutes the
  findings that matter.
- Preferences between container libraries or registration styles.
