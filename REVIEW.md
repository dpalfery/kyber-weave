 **Code Quality**
         - No build errors
         - **NO ANALYZER VIOLATIONS**: Verify all Roslyn and SonarLint analyzer rules pass
           - **Error-level rules must be resolved**: Security (CA3000-3099, S2xxx, S3xxx), Critical bugs (S1xxx)
           - **Warning-level rules must be addressed**: API design (CA1000-1099), Performance (CA1800-1899), Maintainability (CA1500-1599), Code smells (S4xxx)
           - **Demand to see build output**: Require `dotnet build --no-incremental --verbosity minimal` results
           - **Verify no CAxxxx or Sxxxx rule violations exist**
           - **Check for specific analyzer violations by rule ID** (e.g., CA1062, S1135, etc.)
         - No Warnings of any kind. Un resolved warning make me cranky


 **Security**
          - When reviewing code, act as a security auditor. For each function or endpoint, ask these questions:
             1.  **Spoofing (Authentication):** Is the user who they claim to be? Is there a clear login/authentication step?
             2.  **Tampering (Integrity):** Could an attacker change the data in transit or at rest? Is there input validation? Is HTTPS enforced?
             3.  **Repudiation (Logging):** Are there sufficient audit logs? Are logs tamper-resistant? Is user activity logged with a correlation ID instead of raw input?
             4.  **Information Disclosure (Secrets/Data):** Could this code leak secrets (e.g., in logs, errors)? Does it enforce authorization before returning sensitive data?
             5.  **Denial of Service (Resilience):** Could this be abused to crash the service? Is there resource limiting on expensive operations (file uploads, complex calculations)?
             6.  **Elevation of Privilege (Authorization):** Does the code check the user's permissions *every time* it accesses a resource? Can a user access another user's data by changing an ID (Insecure Direct Object Reference)?
          - Incident Response Readiness (Code-Level)
             - **LOGGING:** Ensure logs are structured and include correlation IDs. This is non-negotiable for forensic analysis.
             - **LOG FOR INCIDENTS:** Ensure logs are structured and include correlation IDs. This is non-negotiable for forensic analysis.
*           - **CLEAR ERROR HANDLING:** Code must catch exceptions gracefully without exposing stack traces or internal system details to the end-user.
