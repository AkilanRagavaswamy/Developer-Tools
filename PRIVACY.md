# Forgekit – Developer Toolkit — Privacy Policy

**Effective date:** 5 October 2026
**Publisher:** Akilan Ragavaswamy
**Application:** Forgekit – Developer Toolkit (Microsoft Store)
**Contact:** _[add your public contact email before publishing]_

## Summary

Forgekit is a set of local developer utilities for Windows. **It does not collect, transmit, or share any personal information with the developer or any third party.** There is no account, no sign-in, no telemetry, no analytics, no advertising, no crash reporting, and no update tracking. Everything you put into the app is processed on your own device.

The only time Forgekit sends anything over the network is when **you** use the API tools (API Builder and API Profiler) to contact a server **you** choose. Forgekit is never a recipient of that data.

This policy explains what the app does with data, in plain language, and is provided to meet Microsoft Store Policy 10.5 and applicable privacy laws.

## What information Forgekit handles, and where it goes

Forgekit does not have a server and does not phone home. The developer receives **no** data from the app. The content you work with is handled as follows.

### Tools that are fully offline

The JSON Formatter, JSON Diff Checker, JSON-to-C# generator, and SVG-to-XAML converter run entirely on your device and have no network code. Any text, JSON, SVG, or files you paste, open, or drop into them are processed in memory on your machine and are not sent anywhere.

### API Builder

API Builder sends the HTTP requests **you** compose to the URLs **you** enter. That network traffic goes directly from your machine to the servers you target — the same as any HTTP client (for example, a browser or `curl`). The developer does not see, receive, or intercept it.

- Request and response data is held on your device so you can read it.
- Requests, headers, and settings you save are stored locally on your device (see **Storage and security**).
- Secrets you enter — passwords, bearer tokens, API keys, OAuth client secrets — are stored in the **Windows Credential Manager** (the operating system's secure credential vault), never in a plain file, and never transmitted anywhere except, where applicable, to the endpoint you are authenticating against.

### API Profiler

API Profiler lets you inspect the HTTP calls an application makes, so you can debug and understand network behaviour on your own machine. All captured data stays on your device and is shown only to you; it is not transmitted to the developer or any third party. It works in one of two modes you choose:

- **Proxy mode.** While you are actively capturing, Forgekit runs a local proxy on your machine and temporarily points the **per-user Windows proxy setting** at it so that traffic can be observed. This setting is recorded before it is changed and **restored when you stop** (and, if the app was closed unexpectedly, restored on next launch). To read the contents of HTTPS traffic, you may **optionally** install a local Forgekit root certificate; this happens only when you explicitly choose to, Windows asks you to confirm, and you can remove it at any time from within the app. Captured requests and responses are kept only in memory/on your device for the session.
- **Launch mode.** Forgekit starts a .NET application you select and observes the HTTP calls that application makes, from inside that process, reporting them back to Forgekit on your machine over a private local channel. Nothing leaves your device.

Because these features can observe network traffic and the contents of requests, that traffic **may contain personal information** that belongs to you or appears in the applications you are testing. Forgekit only shows it to you locally; it does not store it beyond your session, and it does not send it anywhere.

## Full-trust and system capabilities

Forgekit is a full-trust Windows desktop app (the `runFullTrust` and `unvirtualizedResources` capabilities). This means it runs with the same access as an ordinary Win32 desktop program — it can read and write files you point it at and make network connections — and it can change the per-user Windows proxy setting as described above. These capabilities are used only to provide the features you invoke, and the only system change Forgekit makes (the proxy setting, and the optional certificate) is reversible and under your control. Forgekit does not use these capabilities to scan your device, collect data in the background, or send anything to the developer.

## Information we collect

**None.** The developer collects no personal information through Forgekit. There is no analytics or telemetry of any kind.

## Storage and security

- Your tool options, and data such as the API Builder workspace you save, are stored **locally** in the app's per-user storage on your device.
- Data you type or paste into a tool is cleared when you close Forgekit; saved options persist according to your settings.
- Secrets are stored in the **Windows Credential Manager**, protected by the operating system.
- Network requests you make with the API tools use the security of the protocol you choose; HTTPS requests are encrypted in transit by the operating system's TLS stack.
- Because no data is sent to the developer, there is no developer-side database to breach.

## How information is shared

Forgekit does **not** sell, rent, or share your information, and does not disclose it to the developer or to any third party. The only outbound data is the API requests you deliberately send to the servers you specify, and any authentication you perform against those servers — all initiated and directed by you.

## Your choices and controls

- Use only the offline tools if you do not want any network activity.
- Clear saved tool state, recent items, and favourites, and reset all settings, from **Settings → Stored data**.
- Remove the optional HTTPS inspection certificate from within the API Profiler at any time.
- Access or delete everything the app has stored by opening the data folder (Settings → Stored data → Open folder) or by uninstalling Forgekit, which removes its local data.
- All of your information is on your own device; you have direct access to it.

## Children's privacy

Forgekit is a developer tool intended for a general, professional audience. It is not directed at children, and it does not knowingly collect any information from children under the age of 13.

## Changes to this policy

We will update this policy as the app's features change, and we will revise the effective date above. The current version is the one published at the URL linked from the Microsoft Store listing.

## Legal compliance

Forgekit is designed to comply with applicable data-protection laws, including the EU General Data Protection Regulation (GDPR) and the California Consumer Privacy Act (CCPA). Because the developer does not collect or process your personal information, there is no developer-held data to access, correct, export, or delete; your data remains under your control on your device.

## Contact

Questions about this policy can be sent to: _[add your public contact email before publishing]_.

---

_This document was prepared to satisfy Microsoft Store Policy 10.5 for a full-trust desktop app. It describes Forgekit's actual behaviour but is not legal advice; review and adapt it to your circumstances before publishing._
