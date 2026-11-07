# Agent Card signing vectors

> **Security note.** The signed payload covers only the canonical form. Members outside the A2A v1.0 Agent Card
> schema (including the v0.3 compatibility fields a2a-python serves) and every value the pruning rule removes
> (`null`, `""`, `[]`, `{}`, `false` on `required`/`pkceRequired`, e.g. a security requirement with no scopes) are
> **not covered by the signature and must not be trusted from a signed card**. This matches a2a-python 1.2.1 and
> a2a-js 1.3.0. `AgentCardCanonicalizer.PrunedPaths` lists the pruned values of a card.
> A signed card with a member that matches a v1.0 field only when case is ignored (`IconUrl`, `Name` next to
> `name`) is reported `Failed` (`AgentCardCanonicalizer.CaseVariantMembers`). `A2ACardResolver` builds the model of
> a verified card from `AgentCardCanonicalizer.SignedContent`, so unsigned members never reach it.

Vectors in `card-signing/` were generated with **a2a-python (`a2a-sdk` on PyPI) 1.2.1** and are checked by
`CanonicalizationVectorTests`. Python is not needed to run the tests. The a2a-js rule was compared by reading its
source; no JS vectors were generated.

## Files

For each vector `{name}`:

- `{name}.input.json`: the raw card JSON given to a2a-python (`ParseDict(input, AgentCard(), ignore_unknown_fields=True)`).
  Some inputs hold values the rule removes (`""`, `[]`, `{}`, `null`, `"required": false`).
- `{name}.canonical.txt`: the exact bytes a2a-python signs: `a2a.utils.signing._canonicalize_agent_card(card)`, UTF-8, no trailing newline.
- `{name}.signed.json`: the signed card as a2a-python serves it (`agent_card_to_dict`, used by its card route).
  For `mixed-version` this includes the v0.3 compatibility fields a2a-python adds (`url`, `preferredTransport`,
  `protocolVersion`, `security`, `supportsAuthenticatedExtendedCard`, `securitySchemes.*.type/in/name`,
  `extensions[].required: false`), none of which are signed.
- `keys.jwks.json`: public keys (`py-es256` ES256 P-256, `py-rs256` RS256 2048-bit). Private keys were thrown away.

| Vector | What it covers |
|---|---|
| `minimal` | Required fields only, `capabilities: {}` |
| `full` | Every optional field, all five security scheme types, two signatures (ES256 + RS256), `jku` header |
| `empty-optionals` | `""`, `[]`, `{}`, `null` in optional and REQUIRED fields, `""` inside string lists |
| `false-capabilities` | `streaming/pushNotifications/extendedAgentCard: false` (kept), `required/pkceRequired: false` (removed) |
| `unicode` | Escapes, control characters, U+2028, astral characters, map keys whose UTF-16 order differs from code point order |
| `extension-params` | `params` Struct with every JSON type, nested empties, `false`/`0`/`"required": false` as data |
| `mixed-version` | A 1.0 + 0.3 interface card; the served form carries unsigned v0.3 compatibility fields |

The generator also checks that each served card verifies with a2a-python's own client path
(`a2a.client.card_resolver.parse_agent_card` + `create_signature_verifier`).

## The rule (confirmed from source, step 1)

### a2a-python 1.2.1

Read: `a2a/utils/signing.py` (`create_agent_card_signer`, `create_signature_verifier`, `_canonicalize_agent_card`,
`_clean_empty`), `a2a/utils/_jcs.py` (`canonicalize`), `a2a/client/card_resolver.py` (`parse_agent_card` and the
`_handle_*_compatibility` helpers), `a2a/server/request_handlers/response_helpers.py` (`agent_card_to_dict`),
and the `lf.a2a.v1` descriptors in `a2a/types/a2a_pb2` for field presence.

`_canonicalize_agent_card(card)` = `canonicalize(_clean_empty(MessageToDict(card) minus "signatures"))`, where `card`
is a protobuf `AgentCard`. The verifier gets that protobuf from `parse_agent_card`, which calls
`ParseDict(..., ignore_unknown_fields=True)`. So the signed payload is:

1. **Members outside the v1.0 schema are dropped** (unknown fields ignored by `ParseDict`).
2. **Proto3 defaults of fields without presence are dropped** by `MessageToDict`: `""` strings, empty repeated/map
   fields, and `false` for the two booleans without presence, `AgentExtension.required` and
   `AuthorizationCodeOAuthFlow.pkceRequired`. Fields declared `optional` keep an explicit default:
   `streaming`, `pushNotifications`, `extendedAgentCard` keep `false`. The Agent Card has no integer or enum fields,
   so `0` and enum zero never occur outside `params`.
3. **`_clean_empty` removes `None`, `""`, `[]` and `{}` recursively**, including list elements, and removes a
   container left empty by that. It also runs inside `params` (`google.protobuf.Struct`), where `false` and `0` are
   kept as data.
4. **REQUIRED fields get no exception.** An empty `name`, `skills: []` or `capabilities: {}` is removed.
5. Top-level `signatures` is removed. RFC 8785 is applied (`_jcs.py`: UTF-16 key order, ECMAScript numbers).
   `params` numbers pass through protobuf `double`, so `1` and `1.0` both canonicalize to `1`.

Consequence: a security requirement with no scopes (`{"schemes": {"mtls": {"list": []}}}`) is removed from the
signed payload entirely (see `full`).

### a2a-js 1.3.0

Read: `dist/index.js` of `@a2a-js/sdk@1.3.0` (bundled from `src/signature.ts`): `canonicalizeAgentCard`,
`cleanEmpty`, `jcsStringify`, and the ts-proto `AgentCard`, `AgentCapabilities`, `AgentExtension`,
`AuthorizationCodeOAuthFlow` `fromJSON`/`toJSON`.

`canonicalizeAgentCard` = `jcsStringify(cleanEmpty(AgentCard.toJSON(AgentCard.fromJSON(card)) minus signatures))`.
Same rule as a2a-python: unknown members dropped by `fromJSON`, `required`/`pkceRequired` written only when not
`false`, `optional` booleans written when set, `cleanEmpty` removes `null`/`undefined`/`""`/`[]`/`{}` recursively
(also inside `params`, which passes through as a plain object). `jcsStringify` sorts keys with the default
(UTF-16) sort and uses `JSON.stringify`, which matches RFC 8785 for valid input.

Differences from a2a-python, none affecting these vectors: a fully empty card canonicalizes to `{}` in JS and `null`
in Python; JS coerces wrongly typed scalars (`Boolean(...)`) where Python's `ParseDict` rejects them.
No `Verbatim` (unpruned) form is needed for either SDK, so it was removed.

### This SDK (`AgentCardCanonicalizer.Canonicalize`)

Applies steps 1 to 5 on the JSON tree using a built-in copy of the v1.0 Agent Card schema. Known differences from
a2a-python, all on inputs a2a-python does not produce:

- snake_case aliases (`default_input_modes`) are treated as unknown and dropped; `ParseDict` would accept them.
- Integers above 2^53 in `params` are formatted as doubles; a2a-python raises `CanonicalizationError`.
- The resolver-side v0.3 rewrites in `parse_agent_card` (building `supportedInterfaces` from `url`, wrapping legacy
  `securitySchemes` by `type`) are not reproduced. They are not part of canonicalization and only matter for
  v0.3-only cards, which carry no v1.0 signature.

`AgentCardSigner` signs cards that contain prunable values (scope-less security requirements, empty `skills`, empty
`capabilities`, empty OAuth `scopes`); those values stay in the served card but are not signed.
`AgentCardEndpoint` logs one warning listing them (`AgentCardCanonicalizer.PrunedPaths`) the first time it signs the card.

## Regenerating

Use a throwaway virtualenv outside the repository. Keep the script in a different directory from the venv and run
Python isolated (`-I`).

```sh
python3 -m venv /tmp/a2a-vectors/venv
/tmp/a2a-vectors/venv/bin/python -I -m pip install "a2a-sdk[signing]==1.2.1"
/tmp/a2a-vectors/venv/bin/python -I gen_vectors.py tests/A2A.Tests/Vectors/card-signing
```

Generated on 2026-11-07 with Python 3.14.8, a2a-sdk 1.2.1, protobuf 7.36.2, PyJWT 2.15.1, cryptography 50.0.2.
Keys are generated on each run, so regenerating replaces every signature and `keys.jwks.json`.
Canonical files do not depend on the keys.

`gen_vectors.py`:

```python
"""Generates Agent Card signing vectors with a2a-sdk (a2a-python) 1.2.1.

Usage: python -I gen_vectors.py <output-dir>
Needs: pip install "a2a-sdk[signing]==1.2.1" (PyJWT, cryptography).
"""
import json
import pathlib
import sys
from importlib.metadata import version

from cryptography.hazmat.primitives.asymmetric import ec, rsa
from google.protobuf.json_format import ParseDict
from jwt.algorithms import ECAlgorithm, RSAAlgorithm

from a2a.client.card_resolver import parse_agent_card
from a2a.server.request_handlers.response_helpers import agent_card_to_dict
from a2a.types import AgentCard
from a2a.utils.signing import (
    _canonicalize_agent_card,
    create_agent_card_signer,
    create_signature_verifier,
)

assert version('a2a-sdk') == '1.2.1', version('a2a-sdk')
out = pathlib.Path(sys.argv[1])
out.mkdir(parents=True, exist_ok=True)

es_key = ec.generate_private_key(ec.SECP256R1())
rs_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
KEYS = {'py-es256': ('ES256', es_key), 'py-rs256': ('RS256', rs_key)}


def public_jwk(kid):
    alg, key = KEYS[kid]
    codec = ECAlgorithm if alg.startswith('ES') else RSAAlgorithm
    jwk = codec.to_jwk(key.public_key(), as_dict=True)
    jwk.update(kid=kid, alg=alg, use='sig')
    return jwk


def signer(kid, jku=None):
    alg, key = KEYS[kid]
    header = {'alg': alg, 'kid': kid, 'typ': 'JOSE'}
    if jku:
        header['jku'] = jku
    return create_agent_card_signer(signing_key=key, protected_header=header)


IFACE = {'url': 'https://agent.example/a2a', 'protocolBinding': 'JSONRPC', 'protocolVersion': '1.0'}
SKILL = {'id': 'echo', 'name': 'Echo', 'description': 'Echoes the input.', 'tags': ['echo']}
BASE = {
    'name': 'Vector Agent',
    'description': 'Agent used for signing vectors.',
    'version': '1.0.0',
    'supportedInterfaces': [IFACE],
    'defaultInputModes': ['text/plain'],
    'defaultOutputModes': ['text/plain'],
    'skills': [SKILL],
}

CARDS = {
    'minimal': (dict(BASE, capabilities={}), ['py-es256'], None),
    'full': (
        {
            'name': 'Full Agent',
            'description': 'Every optional field set.',
            'supportedInterfaces': [
                dict(IFACE, tenant='tenant-1'),
                {'url': 'https://agent.example/rest', 'protocolBinding': 'HTTP+JSON', 'protocolVersion': '1.0'},
            ],
            'provider': {'url': 'https://provider.example', 'organization': 'Example Org'},
            'version': '2.1.0',
            'documentationUrl': 'https://agent.example/docs',
            'capabilities': {
                'streaming': True,
                'pushNotifications': True,
                'extendedAgentCard': True,
                'extensions': [
                    {'uri': 'https://ext.example/a', 'description': 'Extension A', 'required': True, 'params': {'mode': 'strict'}},
                ],
            },
            'securitySchemes': {
                'apiKey': {'apiKeySecurityScheme': {'description': 'API key', 'location': 'header', 'name': 'X-Api-Key'}},
                'bearer': {'httpAuthSecurityScheme': {'description': 'JWT bearer', 'scheme': 'Bearer', 'bearerFormat': 'JWT'}},
                'oauth': {'oauth2SecurityScheme': {
                    'description': 'OAuth',
                    'flows': {'authorizationCode': {
                        'authorizationUrl': 'https://auth.example/authorize',
                        'tokenUrl': 'https://auth.example/token',
                        'refreshUrl': 'https://auth.example/refresh',
                        'scopes': {'read': 'Read access', 'write': 'Write access'},
                        'pkceRequired': True,
                    }},
                    'oauth2MetadataUrl': 'https://auth.example/.well-known/oauth-authorization-server',
                }},
                'device': {'oauth2SecurityScheme': {'flows': {'deviceCode': {
                    'deviceAuthorizationUrl': 'https://auth.example/device',
                    'tokenUrl': 'https://auth.example/token',
                    'scopes': {'read': 'Read access'},
                }}}},
                'oidc': {'openIdConnectSecurityScheme': {'description': 'OIDC', 'openIdConnectUrl': 'https://auth.example/.well-known/openid-configuration'}},
                'mtls': {'mtlsSecurityScheme': {'description': 'Mutual TLS'}},
            },
            'securityRequirements': [{'schemes': {'oauth': {'list': ['read']}}}, {'schemes': {'mtls': {'list': []}}}],
            'defaultInputModes': ['text/plain', 'application/json'],
            'defaultOutputModes': ['text/plain'],
            'skills': [{
                'id': 'search', 'name': 'Search', 'description': 'Searches things.',
                'tags': ['search', 'lookup'], 'examples': ['find cats', 'find dogs'],
                'inputModes': ['text/plain'], 'outputModes': ['application/json'],
                'securityRequirements': [{'schemes': {'bearer': {'list': []}}}],
            }],
            'iconUrl': 'https://agent.example/icon.png',
        },
        ['py-es256', 'py-rs256'],
        'https://agent.example/.well-known/jwks.json',
    ),
    'empty-optionals': (
        dict(
            BASE,
            supportedInterfaces=[dict(IFACE, tenant='')],
            provider={'url': '', 'organization': ''},
            documentationUrl='',
            iconUrl=None,
            capabilities={'extensions': []},
            securitySchemes={},
            securityRequirements=[{'schemes': {}}],
            defaultOutputModes=['text/plain', ''],
            skills=[
                {'id': 'echo', 'name': 'Echo', 'description': '', 'tags': ['', 'echo'], 'examples': [], 'inputModes': [''], 'securityRequirements': []},
            ],
        ),
        ['py-es256'],
        None,
    ),
    'false-capabilities': (
        dict(
            BASE,
            capabilities={
                'streaming': False,
                'pushNotifications': False,
                'extendedAgentCard': False,
                'extensions': [{'uri': 'https://ext.example/opt', 'required': False}],
            },
            securitySchemes={'oauth': {'oauth2SecurityScheme': {'flows': {'authorizationCode': {
                'authorizationUrl': 'https://auth.example/authorize',
                'tokenUrl': 'https://auth.example/token',
                'scopes': {},
                'pkceRequired': False,
            }}}}},
        ),
        ['py-es256'],
        None,
    ),
    'unicode': (
        dict(
            BASE,
            name='Ünïcødé Agent 日本',
            description='Tab\there, newline\nhere, quote " backslash \\ slash / bell \u0007 unit-sep \u001f del \u007f '
                        'line-sep   euro € clef \U0001d11e emoji \U0001f600 </script>',
            securitySchemes={
                '\U0001f600': {'mtlsSecurityScheme': {'description': 'astral key'}},
                'Ａ': {'mtlsSecurityScheme': {'description': 'fullwidth A key'}},
                'é': {'mtlsSecurityScheme': {'description': 'e-acute key'}},
                'Z': {'mtlsSecurityScheme': {'description': 'ascii key'}},
            },
            skills=[dict(SKILL, examples=['¿Qué tal?', '你好', 'a\u0000b'])],
        ),
        ['py-es256'],
        None,
    ),
    'extension-params': (
        dict(
            BASE,
            capabilities={'extensions': [
                {
                    'uri': 'https://ext.example/params',
                    'description': 'Params of every JSON type',
                    'params': {
                        'int': 1, 'negative': -42, 'float': 1.5, 'zero': 0, 'negZero': -0.0, 'small': 1e-7,
                        'large': 1e20, 'maxSafe': 9007199254740991, 'third': 0.1,
                        'true': True, 'false': False, 'required': False, 'null': None,
                        'emptyString': '', 'emptyList': [], 'emptyObject': {},
                        'nested': {'a': {'b': ''}, 'c': [1, '', None, {}, [], 'x'], 'd': {'e': False}},
                        'list': [{'k': 'v'}, {'k': ''}],
                        'é': 'e-acute', '\U0001f600': 'astral', 'Ａ': 'fullwidth',
                    },
                },
                {'uri': 'https://ext.example/empty-params', 'params': {}, 'required': True},
            ]},
        ),
        ['py-es256'],
        None,
    ),
    'mixed-version': (
        dict(
            BASE,
            supportedInterfaces=[IFACE, {'url': 'https://agent.example/v03', 'protocolBinding': 'JSONRPC', 'protocolVersion': '0.3.0'}],
            capabilities={'streaming': True, 'extendedAgentCard': True, 'extensions': [{'uri': 'https://ext.example/opt'}]},
            securitySchemes={'key': {'apiKeySecurityScheme': {'location': 'header', 'name': 'X-Api-Key'}}},
            securityRequirements=[{'schemes': {'key': {'list': []}}}],
        ),
        ['py-es256'],
        None,
    ),
}


def write(path, text):
    path.write_bytes(text.encode('utf-8'))


jwks = {'keys': [public_jwk(k) for k in KEYS]}
write(out / 'keys.jwks.json', json.dumps(jwks, indent=2, ensure_ascii=False) + '\n')

verify = create_signature_verifier(
    key_provider=lambda kid, jku: KEYS[kid][1].public_key(),
    algorithms=['ES256', 'RS256'],
)

for name, (raw, kids, jku) in CARDS.items():
    card = ParseDict(raw, AgentCard(), ignore_unknown_fields=True)
    canonical = _canonicalize_agent_card(card)
    for kid in kids:
        card = signer(kid, jku)(card)
    served = agent_card_to_dict(card)  # what a2a-python's card route returns
    # The served card must verify with a2a-python's own client path.
    reparsed = parse_agent_card(json.loads(json.dumps(served)))
    assert _canonicalize_agent_card(reparsed) == canonical, name
    verify(reparsed)
    write(out / f'{name}.input.json', json.dumps(raw, indent=2, ensure_ascii=False) + '\n')
    write(out / f'{name}.canonical.txt', canonical)
    write(out / f'{name}.signed.json', json.dumps(served, indent=2, ensure_ascii=False) + '\n')
    print(name, 'ok')
```
