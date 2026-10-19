# Handoff — ZipStream (ZIP-P2P)

Documento de transição para o próximo agente/sessão. Resumo do estado, decisões e pendências.

## O que é

Aplicativo desktop multiplataforma para **compactar e transferir arquivos/pastas ponto a ponto (P2P)**, sem nuvem, com **criptografia ponta a ponta (E2EE)**.

## Stack

- **.NET 10** (SDK 10.0.400 instalado na máquina).
- **UI:** Avalonia 12.1.4 (MVVM via CommunityToolkit.Mvvm), ícones `Material.Icons.Avalonia`, i18n via `.resx` (en-US/pt-BR/es-ES).
- **Compressão:** `ZstdSharp.Port` (zstd em chunks), `System.Formats.Tar` (pastas).
- **Transporte P2P:** `DataChannelDotnet` (binding .NET do `libdatachannel` nativo, C++/MPL 2.0).
- **Sinalização:** ASP.NET Core + SignalR.
- **Persistência:** `Microsoft.Data.Sqlite` (`%LOCALAPPDATA%\ZipStream\settings.db`).

## Projetos (solução `ZipP2P.slnx`)

| Projeto | Papel |
|---|---|
| `src/ZipP2P.Core` | Compressão (`.zpp2`) + criptografia (ECDH/AES-GCM/SAS) |
| `src/ZipP2P.P2P` | Transporte (`ITransport`/`LibDataChannelTransport`), sinalização (`SignalRSignalingClient`/`PeerConnection`), transferência (`TransferSession`) |
| `src/ZipP2P.Contracts` | DTOs compartilhados cliente/servidor (`SignalDescription`, `SignalCandidate`) |
| `src/ZipP2P.Server` | Servidor de sinalização (`SignalingHub`) |
| `src/ZipP2P.App` | App Avalonia |
| `tests/ZipP2P.Core.Tests` | 18 testes (compressão + criptografia) |
| `spikes/ZipP2P.Spike` | Spike de integração (transferência + resume + SAS) |

## O que está implementado

- **Compressão/descompressão** de arquivos e pastas em streaming, formato `.zpp2` v2 (header com contentType, chunks com SHA-256, trailer com hash global).
- **Transferência P2P** ponta a ponta (comprimir → enviar → receber → descomprimir) via libdatachannel.
- **E2EE:** ECDH (P-256) + HKDF → AES-256-GCM por chunk, com **SAS** (código de 6 dígitos) para detectar MITM.
- **Resume:** o destinatário informa o offset e o remetente retoma (na camada de protocolo; a orquestração de reconexão ainda não está no app).
- **Servidor local embutido:** o app pode hospedar o servidor de sinalização (configurações → iniciar), com indicador no cabeçalho e porta configurável.
- **Configurações via SQLite** (URL do servidor, porta), editáveis pela UI.
- **Navegação entre telas** (Home/Configurações) com animação de fade.

## Como rodar

```
dotnet test                          # 18 testes
dotnet run --project src/ZipP2P.Server   # servidor standalone (VPS)
dotnet run --project src/ZipP2P.App      # app (pode hospedar o servidor local)
dotnet run --project spikes/ZipP2P.Spike # spike integrado (servidor em processo + 2 peers)
```

Teste na mesma rede: uma máquina inicia o servidor local (app → engrenagem → Configurações → Iniciar servidor local); a outra define a URL do servidor (Configurações) para `http://IP_DA_MAQUINA:5000/signaling`.

## Decisões de arquitetura (fixadas)

1. **Relay-first:** a transferência funciona via relay como baseline; P2P direto é otimização.
2. **Transporte trocável:** isolado em `ITransport` (já trocamos SIPSorcery → libdatachannel por vazão: ~1 MB/s vs ~40-60 MB/s).
3. **E2EE obrigatório** na camada de aplicação (o relay/servidor nunca lê o conteúdo).
4. O `settings.json` foi **substituído** por SQLite + UI (não há mais JSON de config).

## Pendências (por prioridade)

1. **TURN/relay (coturn):** código já aceita ICE servers (`LibDataChannelTransport(iceServers)` + `settings.json` era a config; hoje `IceServers` em SQLite via `AppSettings`). Falta testar com coturn real e expor na UI. `deploy/docker-compose.yml` tem o coturn pronto.
2. **Resume (reconexão):** o protocolo suporta offset, mas falta detectar `Disconnected` e reorquestrar uma nova `PeerConnection` no app (`HomeViewModel`).
3. **Empacotar o app:** `dotnet publish` (Avalonia) → instalador. Ainda é `dotnet run`.
4. **Testes automatizados** das camadas P2P/transferência/sinalização (hoje só spike + testes de Core).
5. **Landing page** (GitHub Pages) — o usuário quer; ver `plan.md` e README.
6. **Monetização** (Stripe, cotas) — por último.

## Gotchas / notas

- **Ícone do app** (`src/ZipP2P.App/Assets/zipstream.ico`) foi gerado a partir de `logo.svg` via SkiaSharp (cor `#3B82F6`). Para regenerar, usar `SKPath.ParseSvgPathData` com o `d` do `logo.svg`.
- **Formato `.zpp2`:** header `magic "ZPP2" | versão(2) | chunkSize | level | contentType`; depois registros `0x01 chunk` e `0x02 trailer`. `contentType` 0=arquivo, 1=pasta.
- **`DataChannelDotnet`** empacota os binários nativos (`runtimes/win-x64`, `linux-x64`, `osx-x64`). Só x64.
- **Threading:** eventos do libdatachannel/SignalR disparam em thread de pool — marshalar para a UI com `Dispatcher.UIThread.Post` (padrão já usado no `HomeViewModel`/`MainViewModel`).
- **ViewLocator** mapeia `*ViewModel` → `*View` (usado na navegação via `ContentControl`).
- **Git:** branch `master`, remote `origin` (HTTPS). Fazer commits em lotes lógicos.

## Referências

- `plan.md` — planejamento completo (fases, segurança, roadmap).
- `README.md` — visão geral e instruções.
