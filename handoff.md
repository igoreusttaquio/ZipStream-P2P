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
| `src/ZipP2P.P2P` | Transporte (`ITransport`/`LibDataChannelTransport`), sinalização (`SignalRSignalingClient`/`PeerConnection`/`SignalingPeerLinkFactory`), transferência (`TransferSession`, `TransferRunner`, `TransferStore`, `TransferPackage`) |
| `src/ZipP2P.Contracts` | DTOs compartilhados cliente/servidor (`SignalDescription`, `SignalCandidate`) |
| `src/ZipP2P.Server` | Servidor de sinalização (`SignalingHub`) |
| `src/ZipP2P.App` | App Avalonia |
| `tests/ZipP2P.Core.Tests` | 18 testes (compressão + criptografia) |
| `tests/ZipP2P.P2P.Tests` | 20 testes: protocolo, retomada, reconexão (transporte em memória com queda injetada) e integração real SignalR + libdatachannel (40 MB) |
| `tests/ZipP2P.App.Tests` | 18 testes: log, linha de comando, menu de contexto (Windows usa chave de registro isolada) |
| `tools/resx_add.py` | Adiciona/atualiza textos nos 3 `.resx` a partir de um JSON |

## O que está implementado

- **Compressão/descompressão** de arquivos e pastas em streaming, formato `.zpp2` v2 (header com contentType, chunks com SHA-256, trailer com hash global).
- **Transferência P2P** ponta a ponta (comprimir → enviar → receber → descomprimir) via libdatachannel.
- **E2EE:** ECDH (P-256) + HKDF → AES-256-GCM por chunk, com **SAS** (código de 6 dígitos) para detectar MITM.
- **Protocolo com controle de fluxo:** janela de 8 MB com `Ack` do destinatário e `Done` ao final; `Abort` com motivo; timeout de inatividade de 30 s. (Bug corrigido: o remetente fechava a conexão com dados ainda no buffer do libdatachannel e arquivos grandes chegavam incompletos.)
- **Retomada:** o cabeçalho leva um `TransferId`; o destinatário responde com o offset do `.part` correspondente. `TransferRunner` reconecta com backoff; `TransferStore` persiste pacote do remetente e parcial do destinatário em `%LOCALAPPDATA%/ZipStream/transfers`, então retoma também após reabrir o app.
- **Sinalização:** no máximo 2 dispositivos por sala; `PeerJoined` também para quem entra depois (necessário na reconexão).
- **UI em abas** (Enviar/Receber/Ferramentas) com `OperationViewModel` comum (status, progresso, erro amigável + "Ver log"), lista de interrompidos com Retomar/Descartar, pasta de destino escolhida e lembrada.
- **Log técnico** (`AppLog`, `ILoggerProvider` em memória) com Copiar/Exportar; P2P/WebRTC/sinalização logam via `ILoggerFactory`.
- **Bandeja + instância única:** `ShutdownMode.OnExplicitShutdown`, `TrayIcon` (Abrir/Sair), fechar a janela só esconde (`AppSettings.CloseToTray`). `SingleInstance` (named pipe `CurrentUserOnly`) repassa `--send`/`--receive` para a instância aberta. `--send` abre `QuickSendWindow` (código copiado automaticamente), sem a janela principal.
- **Menu de contexto** (`ShellIntegration/`): Windows (HKCU), Linux (Nautilus/Caja/Nemo/Dolphin), macOS (Ação Rápida, experimental, não testada em Mac real). Linha de comando `--send`/`--receive`.
- **Servidor local embutido:** o app pode hospedar o servidor de sinalização (configurações → iniciar), com indicador no cabeçalho e porta configurável.
- **Configurações via SQLite** (URL do servidor, porta), editáveis pela UI.
- **Navegação entre telas** (Home/Configurações/Log) com animação de fade.

## Como rodar

```
dotnet test                              # 56 testes
dotnet run --project src/ZipP2P.Server   # servidor standalone (VPS)
dotnet run --project src/ZipP2P.App      # app (pode hospedar o servidor local)
```

Teste na mesma rede: uma máquina inicia o servidor local (app → engrenagem → Configurações → Iniciar servidor local); a outra define a URL do servidor (Configurações) para `http://IP_DA_MAQUINA:5000/signaling`.

## Decisões de arquitetura (fixadas)

1. **Relay-first:** a transferência funciona via relay como baseline; P2P direto é otimização.
2. **Transporte trocável:** isolado em `ITransport` (já trocamos SIPSorcery → libdatachannel por vazão: ~1 MB/s vs ~40-60 MB/s).
3. **E2EE obrigatório** na camada de aplicação (o relay/servidor nunca lê o conteúdo).
4. O `settings.json` foi **substituído** por SQLite + UI (não há mais JSON de config).

## Pendências (por prioridade)

1. **TURN/relay (coturn):** o código aceita ICE servers (`IceServers` em SQLite via `AppSettings`), mas não há tela nem teste com coturn real. `deploy/docker-compose.yml` tem o coturn pronto.
2. **Empacotar o app:** instalador (o menu de contexto aponta para o executável atual; depois de instalar, reinstalar a entrada em Configurações).
4. **Envio a partir da pasta original:** hoje o remetente comprime para um `.zpp2` em cache antes de enviar (precisa de espaço em disco ≈ tamanho comprimido).
5. **Landing page** (`docs/index.html`) — em melhoria.
6. **Monetização** (Stripe, cotas) — por último.

## Gotchas / notas

- **Ícone do app** (`src/ZipP2P.App/Assets/zipstream.ico`) foi gerado a partir de `logo.svg` via SkiaSharp (cor `#3B82F6`). Para regenerar, usar `SKPath.ParseSvgPathData` com o `d` do `logo.svg`.
- **Formato `.zpp2`:** header `magic "ZPP2" | versão(2) | chunkSize | level | contentType`; depois registros `0x01 chunk` e `0x02 trailer`. `contentType` 0=arquivo, 1=pasta.
- **`DataChannelDotnet`** empacota os binários nativos (`runtimes/win-x64`, `linux-x64`, `osx-x64`). Só x64.
- **Threading:** eventos do libdatachannel/SignalR disparam em thread de pool — marshalar para a UI com `Dispatcher.UIThread.Post` (padrão usado em `SendViewModel`/`ReceiveViewModel`).
- **Mensagens antes do assinante:** `LibDataChannelTransport` guarda mensagens recebidas antes de alguém assinar `MessageReceived` (o par pode mandar a chave pública assim que o canal abre).
- **Build x Visual Studio:** não compilar pela linha de comando com o app aberto pelo VS — os dois disputam `bin/obj` e um build interrompido gera DLL sem XAML compilado ("No precompiled XAML found"). Resolver com Rebuild.
- **Acessibilidade:** botões com ícone + texto precisam de `AutomationProperties.Name` (senão leitores de tela/automação leem "Avalonia.Controls.StackPanel").
- **ViewLocator** mapeia `*ViewModel` → `*View` (usado na navegação via `ContentControl`).
- **Git:** branch `master`, remote `origin` (HTTPS). Fazer commits em lotes lógicos.

## Referências

- `plan.md` — planejamento completo (fases, segurança, roadmap).
- `README.md` — visão geral e instruções.
