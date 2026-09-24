# Pulumi (Infrastructure-as-Code) Review Best Practices

## Avoid Anti-Patterns with Outputs & Resources
- **Resource Creation:** Watch for resources being created inside an asynchronous callback or `Output.apply()`. This breaks dependency tracking. Pass Output values directly as inputs to other resources instead.

## Component Reusability & Organization
- **ComponentResource:** Encourage use of `ComponentResource` classes to group related cloud resources into logical units for clarity, reuse, and proper parent-child relationships. Check if new code can leverage existing modules.

## State & Secrets Management
- **Secret Values:** Ensure the code uses Pulumi's secrets management features (e.g., `pulumi.Config` with `config.requireSecret()`) for sensitive data, so they are encrypted in state files.
- **No Plaintext Secrets:** Check that no plaintext secrets or keys are hardcoded. Use Pulumi's `--secret` flag for setting secret values.
- **State Security:** Confirm state is managed properly (e.g., remote backend, not committed to source control).

## Idempotence & Safety
- **Idempotence:** Pulumi code should be idempotent. Multiple runs should produce the same infrastructure state.
- **Random Naming:** If unique naming is required, ensure it is handled using `pulumi.Random` to track state properly and avoid recreating resources on each run.

## Cloud Best Practices & Performance
- **Tags/Labels:** Confirm important resources are tagged or labeled for identification and cost tracking.
- **Resource Limits:** Verify that large resources (VMs, clusters) adhere to planned sizing and quotas.
- **Parallelism & Dependencies:** Ensure unnecessary explicit dependencies (`dependsOn`) are not forcing sequential operations when parallel is safe. Confirm real dependencies are properly expressed via passing outputs.

## Testing & Previews
- **Unit Tests:** Check for unit tests (e.g., `pulumi.runtime.test`) covering new infrastructure code.
- **Preview Results:** Ensure the PR description includes results from a `pulumi preview` to highlight unexpected changes.
