# ZipStream

Aplicativo desktop multiplataforma para **compactar e transferir arquivos/pastas ponto a ponto (P2P)**, sem depender de armazenamento em nuvem e com **criptografia ponta a ponta (E2EE)**.

## O que ele faz

- Compacta/descompacta arquivos e pastas em **streaming** (sem carregar tudo na RAM), usando zstd.
- Transfere arquivos grandes **P2P** (WebRTC DataChannel via libdatachannel), com fallback a **TURN/relay**.
- **E2EE**: ECDH + AES-256-GCM por chunk + SAS (código de verificação) contra MITM.
- Integridade: hash SHA-256 por chunk + hash global.
- UI em Avalonia com temas claro/escuro e i18n (en-US, pt-BR, es-ES).

## Arquitetura

```
[App Origem] ──DataChannel P2P (libdatachannel)── [App Destino]
      │                                              │
      └────── SignalR (sinalização) ────────────────┘
                        │
              [Servidor de sinalização]
              + coturn (TURN/STUN, fallback)
```

O servidor **só pareia** os dois lados (troca SDP/candidatos); o arquivo vai **direto entre as máquinas** (ou via relay quando o P2P direto é bloqueado).

## Projetos

| Projeto | Papel |
|---|---|
| `src/ZipP2P.Core` | Motor de compressão (zstd streaming) + criptografia (ECDH/AES-GCM/SAS) |
| `src/ZipP2P.P2P` | Transporte (libdatachannel), sinalização e protocolo de transferência |
| `src/ZipP2P.Contracts` | DTOs compartilhados (cliente/servidor) |
| `src/ZipP2P.Server` | Servidor de sinalização (ASP.NET Core + SignalR) |
| `src/ZipP2P.App` | Aplicativo desktop (Avalonia) |
| `tests/ZipP2P.Core.Tests` | Testes do motor e da criptografia |
| `spikes/ZipP2P.Spike` | Spike de integração (transferência ponta a ponta) |

## Requisitos

- .NET 10 SDK
- (opcional, para relay) Docker — para o coturn

## Como rodar

### 1. Servidor de sinalização

```bash
dotnet run --project src/ZipP2P.Server
```

Escuta em `http://0.0.0.0:5000` (hub em `/signaling`).

### 2. Aplicativo

```bash
dotnet run --project src/ZipP2P.App
```

Configuração em `settings.json` (copiado para `bin/Debug/net10.0/`):

```json
{
  "SignalingUrl": "http://localhost:5000/signaling",
  "IceServers": []
}
```

- `IceServers` vazio → P2P direto (mesma rede).
- Para internet, adicione STUN/TURN (ex.: `"stun:stun.l.google.com:19302"`, `"turn:usuario:senha@seu-ip:3478"`).

## Testar na mesma rede (2 máquinas)

1. **Máquina A**: rode o servidor (`dotnet run --project src/ZipP2P.Server`).
2. **Máquina B**: no `settings.json`, troque o `SignalingUrl` para o IP da máquina A (ex.: `http://192.168.0.10:5000/signaling`).
3. **Máquina A**: app → modo **Enviar** → escolha o arquivo/pasta → **Enviar** (gera um código).
4. **Máquina B**: app → modo **Receber** → digite o código → **Receber** (os arquivos vão para `%TEMP%\ZipStream-Recebidos`).

O código de segurança (SAS) aparece nas duas telas — confira se é o mesmo.

## Testar entre redes (TURN/relay)

1. Suba o coturn: `docker compose -f deploy/docker-compose.yml up -d` (em uma VPS/máquina pública).
2. No `settings.json` das duas máquinas, configure:
   ```json
   {
     "SignalingUrl": "http://IP_DA_VPS:5000/signaling",
     "IceServers": ["stun:IP_DA_VPS:3478", "turn:zipstream:SUA_SENHA@IP_DA_VPS:3478"]
   }
   ```

## Testes

```bash
dotnet test
```

## Roadmap (resumido)

- [x] Compressão/descompressão em streaming (arquivos e pastas)
- [x] Transferência P2P + sinalização
- [x] E2EE (ECDH + AES-GCM) + SAS
- [x] Configuração de STUN/TURN
- [ ] Resume (reconexão/re-envio de chunks)
- [ ] Deploy do servidor em produção (HTTPS)
- [ ] Empacotamento do app (instalador)
- [ ] Monetização

Detalhes no [`plan.md`](plan.md).
