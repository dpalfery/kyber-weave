---
name: password-reset
description: Use to reset an employee's corporate account password following the IT security SOP. Use when a user reports they are locked out, forgot their password, or needs a password reset. Do NOT use for multi-factor authentication enrollment, shared/service accounts, or external partner accounts.
license: MIT
metadata:
  author: it-platform-team
  version: 1.2.0
---

# Password Reset SOP

## When to use
A user is locked out of, or cannot sign in to, their corporate account and needs their password reset. For service/shared accounts or MFA enrollment, route elsewhere.

## Procedure
1. Verify the requester's identity using the approved knowledge-based verification questions.
2. Confirm the account is a standard corporate account (not a service or shared account).
3. Trigger the reset via the self-service portal action.
4. Send the temporary credential over the approved secure channel only.

## Rules
- ALWAYS verify identity before initiating any reset.
- NEVER send a temporary password over an unverified or external channel.
- MUST require a forced change at next sign-in.

## Example
A user messages: "I'm locked out and can't get into my laptop." Verify identity, confirm it is a standard account, trigger the portal reset, and deliver the temporary credential securely with a forced change on first login.
