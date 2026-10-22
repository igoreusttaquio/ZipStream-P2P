# ZipStream

Aplicativo desktop multiplataforma (Windows, Linux, macOS) para **compactar e transferir arquivos e pastas direto entre dois computadores (P2P)**, sem armazenar nada em nuvem e com **criptografia ponta a ponta (E2EE)**.

## O que ele faz

- **Envia arquivos ou pastas** de qualquer tamanho: compacta em streaming (zstd), cifra e envia direto para o outro computador.
- **Código de envio** de 6 caracteres para parear os dois lados e **código de segurança (SAS)** para conferir que ninguém está no meio.
- **Retomada automática:** se a conexão cair, reconecta e continua de onde parou. Funciona inclusive depois de **fechar e reabrir o app** (lista "Envios/Recebimentos interrompidos" → **Retomar**).
- **Escolha onde salvar** ao receber (padrão: `Downloads`); nunca sobrescreve — usa `nome (1)`, `nome (2)`...
- **Menu de contexto** "Enviar com ZipStream" no gerenciador de arquivos (Windows; Linux e macOS em melhor esforço).
- **Log técnico** com o que aconteceu em cada etapa (conexão, ICE, transferência, erros), com **Copiar** e **Exportar**.
- **Ferramentas:** compactar/descompactar `.zpp2` localmente, sem rede.
- Temas claro/escuro e idiomas en-US, pt-BR e es-ES.

## Como usar

### Enviar

1. Aba **Enviar** → arraste um arquivo ou pasta, ou use **Escolher arquivo** / **Escolher pasta**.
   (Ou clique com o botão direito no arquivo → **Enviar com ZipStream**.)
2. **Gerar código e enviar** → passe o **código de envio** para quem vai receber.
3. Quando o outro lado conectar, confira se o **código de segurança** é o mesmo nas duas telas.

### Receber

1. Aba **Receber** → digite o código de envio.
2. Em **Salvar em**, escolha a pasta (fica lembrada para as próximas vezes).
3. **Receber** → ao final, **Abrir pasta**.

### Se algo der errado

- O painel de status mostra uma explicação e o detalhe técnico; **Ver log** abre o log completo.
- Transferências interrompidas aparecem na aba correspondente com **Retomar** (os dois lados precisam retomar; o código é o mesmo).

## Arquitetura

```
[App Origem] ──DataChannel P2P (libdatachannel)── [App Destino]
      │                                              │
      └────── SignalR (sinalização) ────────────────┘
                        │
              [Servidor de sinalização]
              + coturn (TURN/STUN, fallback)
```

O servidor **só pareia** os dois lados (troca de SDP/candidatos ICE; no máximo 2 dispositivos por código). O arquivo vai **direto entre as máquinas** (ou via relay TURN quando o P2P direto é bloqueado), sempre cifrado com AES-256-GCM.

### Protocolo de transferência

Cada pacote `.zpp2` (zstd em chunks com SHA-256) é enviado em mensagens de até 60 KiB:

1. Troca de chaves ECDH P-256 → chave AES-256-GCM + código SAS.
2. Remetente envia o cabeçalho (id da transferência, nome, tamanho).
3. Destinatário responde com quantos bytes desse id já tem (**retomada**).
4. Remetente envia os chunks com **controle de fluxo** (no máximo 8 MB sem confirmação); o destinatário confirma (`Ack`) o que gravou em disco e, ao final, envia `Done`.
5. Queda de conexão ou 30 s sem resposta → reconexão automática com backoff, retomando do último byte confirmado.

O estado das transferências em andamento fica em `%LOCALAPPDATA%/ZipStream/transfers` (Linux: `~/.local/share/ZipStream/transfers`; macOS: `~/Library/Application Support/ZipStream/transfers`).

## Projetos

| Projeto | Papel |
|---|---|
| `src/ZipP2P.Core` | Motor de compressão (zstd streaming, formato `.zpp2`) + criptografia (ECDH/AES-GCM/SAS) |
| `src/ZipP2P.P2P` | Transporte (libdatachannel), sinalização, protocolo de transferência, reconexão e estado persistido |
| `src/ZipP2P.Contracts` | DTOs compartilhados (cliente/servidor) |
| `src/ZipP2P.Server` | Servidor de sinalização (ASP.NET Core + SignalR) |
| `src/ZipP2P.App` | Aplicativo desktop (Avalonia) |
| `tests/ZipP2P.Core.Tests` | Testes do motor e da criptografia |
| `tests/ZipP2P.P2P.Tests` | Protocolo, retomada, reconexão e integração ponta a ponta via WebRTC |
| `tests/ZipP2P.App.Tests` | Log, linha de comando e integração com o menu de contexto |

## Requisitos

- .NET 10 SDK
- (opcional, para relay) Docker — para o coturn

## Como rodar

### Servidor de sinalização

```bash
dotnet run --project src/ZipP2P.Server
```

Escuta em `http://0.0.0.0:5000` (hub em `/signaling`). Para a mesma rede, também dá para usar o servidor **embutido no app**: ⚙️ Configurações → **Iniciar servidor local**.

### Aplicativo

```bash
dotnet run --project src/ZipP2P.App
```

Linha de comando:

| Argumento | Efeito |
|---|---|
| `--send <caminho>` | Abre a aba Enviar com o item e já começa a enviar (usado pelo menu de contexto) |
| `--receive <código>` | Abre a aba Receber com o código preenchido |
| `<caminho>` | Igual a `--send` |

As configurações ficam em SQLite (`%LOCALAPPDATA%/ZipStream/settings.db`) e são editadas em ⚙️ **Configurações**: URL do servidor, porta do servidor local, menu de contexto. A pasta de destino é lembrada pela aba Receber.

## Testar na mesma rede (2 máquinas)

1. **Máquina A:** ⚙️ Configurações → **Iniciar servidor local** (mostra o endereço, ex.: `http://192.168.0.10:5000/signaling`).
2. **Máquina B:** ⚙️ Configurações → **URL do servidor** = endereço da máquina A → **Salvar**.
3. Uma máquina **Envia** (gera o código), a outra **Recebe** com o código.

## Testar entre redes (TURN/relay)

1. Suba o servidor e o coturn numa máquina pública: `docker compose -f deploy/docker-compose.yml up -d`.
2. Nas duas máquinas, **URL do servidor** = `http://IP_DA_VPS:5000/signaling`.
3. Servidores ICE (STUN/TURN): ainda **sem tela** — grave a chave `IceServers` (lista JSON) no `settings.db`:
   ```sql
   INSERT OR REPLACE INTO settings(key, value)
   VALUES ('IceServers', '["stun:IP_DA_VPS:3478", "turn:zipstream:SUA_SENHA@IP_DA_VPS:3478"]');
   ```

## Menu de contexto

⚙️ Configurações → **Menu de contexto** → **Instalar** / **Remover**.

| Sistema | Como é instalado | Observações |
|---|---|---|
| Windows | `HKCU\Software\Classes\*\shell` e `Directory\shell` (sem administrador) | No Windows 11 fica em "Mostrar mais opções" |
| Linux | Scripts do Nautilus e Caja, ação do Nemo, service menu do Dolphin | Melhor esforço; pode ser preciso reiniciar o gerenciador (`nautilus -q`) |
| macOS | Ação Rápida do Automator em `~/Library/Services` | Experimental; melhor com o app em /Applications |

Se o app mudar de lugar, Configurações mostra que a entrada aponta para outra cópia — é só clicar em **Instalar** de novo.

## Testes

```bash
dotnet test
```

## Roadmap (resumido)

- [x] Compressão/descompressão em streaming (arquivos e pastas)
- [x] Transferência P2P + sinalização
- [x] E2EE (ECDH + AES-GCM) + SAS
- [x] Arquivos grandes (controle de fluxo + confirmação)
- [x] Retomada automática e após reabrir o app
- [x] Log técnico, menu de contexto, escolha da pasta de destino
- [ ] Tela para servidores STUN/TURN + teste com coturn real
- [ ] Deploy do servidor em produção (HTTPS)
- [ ] Empacotamento do app (instalador)
- [ ] Monetização

Detalhes no [`plan.md`](plan.md).
