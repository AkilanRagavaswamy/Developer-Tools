# ForgeKitRk - Developer Toolkit — Privacy Policy

**Effective date:** 6 October 2026
**Publisher:** Akilan Ragavaswamy
**Application:** ForgeKitRk - Developer Toolkit (Microsoft Store)
**Contact:** _[add your public contact email before publishing]_

## Summary

ForgeKitRk is a set of local developer utilities for Windows. **It does not collect, transmit, or share any personal information with the developer or any third party.** There is no account, no sign-in, no telemetry, no analytics, no advertising, no crash reporting, and no update tracking. Everything you put into the app is processed on your own device.

The only time ForgeKitRk sends anything over the network is when **you** use the API Builder to contact a server **you** choose. ForgeKitRk is never a recipient of that data.

This policy explains what the app does with data, in plain language, and is provided to meet Microsoft Store Policy 10.5 and applicable privacy laws.

## What information ForgeKitRk handles, and where it goes

ForgeKitRk does not have a server and does not phone home. The developer receives **no** data from the app. The content you work with is handled as follows.

### Tools that are fully offline

Every tool other than API Builder runs entirely on your device and has no network code: the JSON Formatter, JSON Diff Checker, JSON to C#, JSON to Table, SQL Formatter, XML Formatter, Date & Unix Time converter, Base64 Text, Base64 Image, URL Encoder, HTML Encoder, UUID Generator, QR Code Generator, Text Compare, Regex Validator, Character Counter and SVG to XAML. (Markdown Preview and HTML Viewer are offline too, but they render with a web component, so they are described separately below.) Any text, images or files you paste, open or drop into them are processed in memory on your machine and are not sent anywhere.

### Markdown Preview and HTML Viewer

Markdown Preview and HTML Viewer show the rendered document in Microsoft Edge WebView2, the web-rendering component that is part of Windows. ForgeKitRk turns off script in the preview (in HTML Viewer you can turn on the page's own scripts, which still cannot fetch anything), gives the page a Content-Security-Policy that allows only its own inline styling and embedded `data:` images, and refuses every request the page makes — so a document cannot make the preview load an image, a stylesheet or anything else from the internet. If you click a link in the preview, it opens in your own web browser, which is then subject to that browser's own privacy terms. WebView2 itself is a Microsoft component; any diagnostic data it may send is governed by Microsoft's privacy statement and your Windows diagnostic-data settings, not by ForgeKitRk.


### API Builder

API Builder sends the HTTP requests **you** compose to the URLs **you** enter. That network traffic goes directly from your machine to the servers you target — the same as any HTTP client (for example, a browser or `curl`). The developer does not see, receive, or intercept it.

- Request and response data is held on your device so you can read it.
- Requests, headers, and settings you save are stored locally on your device (see **Storage and security**).
- Secrets you enter — passwords, bearer tokens, API keys, OAuth client secrets — are stored in the **Windows Credential Manager** (the operating system's secure credential vault), never in a plain file, and never transmitted anywhere except, where applicable, to the endpoint you are authenticating against.

## Full-trust capability

ForgeKitRk is a full-trust Windows desktop app (the `runFullTrust` capability). This means it runs with the same access as an ordinary Win32 desktop program — it can read and write files you point it at and make the network connections you ask for. This capability is used only to provide the features you invoke. ForgeKitRk does not change system settings, scan your device, collect data in the background, or send anything to the developer.

## Information we collect

**None.** The developer collects no personal information through ForgeKitRk. There is no analytics or telemetry of any kind.

## Storage and security

- Your tool options, and data such as the API Builder workspace you save, are stored **locally** in the app's per-user storage on your device.
- Data you type or paste into a tool is cleared when you close ForgeKitRk; saved options persist according to your settings.
- Secrets are stored in the **Windows Credential Manager**, protected by the operating system.
- Network requests you make with the API Builder use the security of the protocol you choose; HTTPS requests are encrypted in transit by the operating system's TLS stack.
- Because no data is sent to the developer, there is no developer-side database to breach.

## How information is shared

ForgeKitRk does **not** sell, rent, or share your information, and does not disclose it to the developer or to any third party. The only outbound data is the API requests you deliberately send to the servers you specify, and any authentication you perform against those servers — all initiated and directed by you.

## Your choices and controls

- Use only the offline tools if you do not want any network activity.
- Clear saved tool state, recent items, and favourites, and reset all settings, from **Settings → Stored data**.
- Access or delete everything the app has stored by opening the data folder (Settings → Stored data → Open folder) or by uninstalling ForgeKitRk, which removes its local data.
- All of your information is on your own device; you have direct access to it.

## Children's privacy

ForgeKitRk is a developer tool intended for a general, professional audience. It is not directed at children, and it does not knowingly collect any information from children under the age of 13.

## Changes to this policy

We will update this policy as the app's features change, and we will revise the effective date above. The current version is the one published at the URL linked from the Microsoft Store listing.

## Legal compliance

ForgeKitRk is designed to comply with applicable data-protection laws, including the EU General Data Protection Regulation (GDPR) and the California Consumer Privacy Act (CCPA). Because the developer does not collect or process your personal information, there is no developer-held data to access, correct, export, or delete; your data remains under your control on your device.

## Contact

Questions about this policy can be sent to: _akilanrkpersonal@gmail.com_.

---

_This document was prepared to satisfy Microsoft Store Policy 10.5 for a full-trust desktop app. It describes ForgeKitRk's actual behaviour but is not legal advice; review and adapt it to your circumstances before publishing._
