# ADR-0005: حالت‌های Mock و Connected

- Status: Accepted with blockers
- Date: 2026-08-16

## Context

credential و contract واقعی dependencyهای پستی در repository قابل نگهداری نیست و برخی artefactهای رسمی هنوز تحویل نشده‌اند.

## Decision

portهای Application در local با Mock deterministic و در محیط متصل با adapterهای HTTP/SQL/SOAP اجرا می‌شوند. selector canonical برابر `Edge:Integrations:Profile` است: `Mock`، `LocalConnected`، `Connected` یا `Custom`. `Connected` هر سه adapter را Real می‌کند؛ alias قدیمی `Mode=Real` برای سازگاری خوانده می‌شود.

default کد `LocalConnected` است تا adapter مقصد زودتر contract-test شود؛ محیط offline/CI باید `Mock` را صریح تنظیم کند. `Custom` بدون mode صریح هر سه component fail-fast می‌شود.

## Consequences

- توسعه و CI بدون secret ممکن است.
- موفقیت Mock اثبات سازگاری واقعی نیست.
- Connected rollout تا SQL schema/SP، SOAP WSDL، label template، reference data و check-digit rule Blocked است.
- mock در Production ممنوع و باید fail-fast/monitor شود.

## Verification

contract test در sandbox مجاز، timeout/fault injection و log redaction پیش از rollout اجباری است.
