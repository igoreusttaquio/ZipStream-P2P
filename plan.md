# 📋 PLANEJAMENTO DE DESENVOLVIMENTO: ZIP-P2P (C# / .NET Moderno)

## 🎯 Visão Geral do Projeto
Criar um aplicativo multiplataforma (Windows, macOS, Linux) de compactação e descompactação de arquivos com funcionalidade integrada de transferência via streaming P2P / Relay.

O sistema utiliza a máquina do remetente como fonte do streaming (sem armazenar arquivos na nuvem), intermediado por uma VPS central para sinalização de rede, autenticação e suporte a relay/TURN.

---

## 🏛️ Arquitetura do Sistema

```
[ Cliente Origem (App .NET) ]  <--- WebRTC DataChannel (P2P Direto) --->  [ Cliente Destino (App .NET) ]
│                                                                       │
└──────────────── (SignalR / WebSocket - Signaling) ────────────────────┘
                                    │
                          [ VPS Central ]
                          - API REST + SignalR (ASP.NET Core)
                          - TURN/STUN Relay (coturn) - Fallback
                          - Identity / Auth (JWT)
                          - Gateway de Pagamento (Stripe.NET)
                          - Banco (PostgreSQL / EF Core)
```

### Componentes:
1. **Desktop Client App:** Avalonia UI + motor de I/O em streaming (chunks), WebRTC (SIPSorcery) e compressão (ZstdSharp).
2. **VPS Backend (ASP.NET Core):**
   - **SignalR Hub (Signaling):** estabelece o túnel entre origem e destino (troca de SDP/ICE).
   - **TURN/STUN Server (coturn):** retransmite os dados se o P2P direto for bloqueado por NAT/Firewall.
   - **API & Auth Server:** gerencia usuários, sessões, limites de tamanho e pagamentos.

---

## 🛠️ Stack Tecnológica Recomendada

- **Desktop App (Avalonia + .NET 8 LTS):**
  - **Framework UI:** Avalonia 11 (XAML-like, MVVM via CommunityToolkit.Mvvm).
  - **Engine de Compressão:** ZstdSharp (zstd) sobre streams/buffers em chunks.
  - **Integridade:** SHA-256 incremental (`System.Security.Cryptography`).
  - **Comunicação P2P:** SIPSorcery (WebRTC DataChannels) + SignalR client (signaling).
  - **Pipeline de chunks:** `System.Threading.Channels` (produtor/consumidor, backpressure).
  - **Protocolo binário:** MessagePack-CSharp (framing compacto de chunks).

- **VPS Infraestrutura & Backend:**
  - **Sistema Operacional:** Ubuntu Server / Debian na VPS.
  - **Backend API & Signaling:** ASP.NET Core 8 (Web API + SignalR).
  - **Auth:** ASP.NET Core Identity + JWT.
  - **Banco de Dados:** PostgreSQL (prod) / SQLite (dev) via EF Core.
  - **Servidor TURN/STUN:** coturn (nativo Linux para fallback de rede).
  - **Proxy Reverso / SSL:** Nginx / Caddy com Let's Encrypt.
  - **Pagamentos:** Stripe.NET.

> ⚠️ **Risco a monitorar:** SIPSorcery é a principal lib .NET para WebRTC DataChannel, mas é menos madura que as libs JS/nativas. Plano B: binding nativo do `libdatachannel` (C++) via P/Invoke caso o SIPSorcery não atenda performance/estabilidade no teste de 10GB+.

---

## 🚀 Fases de Desenvolvimento

### FASE 1: Motor de Compressão em Streaming (Local)
Objetivo: Compactar/descompactar em tempo real usando buffers de memória sem travar a interface.

- [ ] Configurar projeto base Desktop (Avalonia + .NET 8).
- [ ] Implementar compressão por blocos/chunks (ex: blocos de 4MB com ZstdSharp).
- [ ] Criar pipeline de I/O assíncrono (`Channel<T>` + `Stream`/`PipeReader`) para leitura/gravação em disco.
- [ ] Implementar verificação de integridade por chunk (SHA-256 incremental).
- [ ] *Entregável:* App local que compacta/descompacta arquivos grandes gerando stream de blocos, com UI responsiva.

### FASE 2: Infraestrutura da VPS & Protocolo de Sinalização (Signaling)
Objetivo: Preparar a VPS para conectar dois clientes e intermediar o handshake de rede.

- [ ] Configurar a VPS (Docker, Nginx/Caddy, Firewall/UFW).
- [ ] Desenvolver o SignalR Hub para pareamento via códigos/tokens curtos (ex: `app.link/ABCD-1234`).
- [ ] Instalar e configurar o coturn (STUN/TURN) para furos de NAT.
- [ ] Implementar autenticação por chave/sessão temporária (JWT de curta duração).
- [ ] *Entregável:* VPS com signaling + TURN prontos para estabelecer conexão entre duas instâncias do app.

### FASE 3: Transmissão P2P e Fallback de Relay
Objetivo: Enviar blocos compactados da origem ao destino via WebRTC (ou Relay na VPS).

- [ ] Implementar client WebRTC DataChannels com SIPSorcery no app.
- [ ] Integrar o motor de compressão (Fase 1) com os canais de dados de rede.
- [ ] Criar protocolo de transferência:
  - **Manifesto inicial:** metadados (nome, tamanho total, nº de chunks, hash global, chave de sessão).
  - **Transferência de chunks:** envio sequencial/paralelo com ACK (confirmação).
  - **Resume:** reconexão em caso de queda de internet, retomando do último chunk confirmado.
- [ ] Implementar fallback automático para o Relay (TURN) da VPS se o P2P direto falhar.
- [ ] Criptografia End-to-End (E2EE) das streams antes da transmissão.
- [ ] *Entregável:* Envio bem-sucedido de um arquivo de 10GB+ entre duas máquinas distantes.

### FASE 4: Interface do Usuário (UI/UX) & Experiência
Objetivo: App intuitivo, simples e com feedback visual em tempo real.

- [ ] **Tela Principal:** Drag & Drop de arquivos/pastas; seleção de nível de compressão (Velocidade vs. Tamanho).
- [ ] **Tela de Envio/Streaming:** geração do link/código; progresso em tempo real (MB/s, %, ETA, status P2P/Relay).
- [ ] **Tela de Recebimento:** pré-visualização da estrutura antes de baixar; escolha do diretório e botão "Iniciar".
- [ ] *Entregável:* Aplicação visualmente pronta e agradável.

### FASE 5: Monetização, Limites & Medição de Banda
Objetivo: Implementar modelo freemium / pagamento por envio ou assinatura.

- [ ] Integrar Stripe.NET na VPS.
- [ ] Implementar regras de limitação:
  - **Grátis:** transmissão limitada a X GB (ex: 2 GB) ou taxa limitada (ex: 2 MB/s).
  - **Pago:** links Pro/Premium com transmissão ilimitada e alta velocidade (prioridade no TURN).
- [ ] Sistema de validação de tokens/licenças no cliente.
- [ ] *Entregável:* Fluxo completo de cobrança e liberação de envio de arquivos grandes.

---

## 🎨 Interface (Design Reference)

Tema escuro, accent em um tom (azul/roxo) para ações; status por cor: verde (P2P), âmbar (Relay), vermelho (erro/quedas).

### Tela Principal — Home
```
┌─────────────────────────────────────────────────────────────┐
│  ZIP-P2P                                          ⚙  👤    │
│      ┌───────────────────────────────────────────┐          │
│      │         📂  Arraste arquivos/pasta        │          │
│      │             ou clique para escolher       │          │
│      └───────────────────────────────────────────┘          │
│      Nível de compressão                                    │
│      ○ Rápido   ◉ Equilibrado   ○ Máximo                    │
│      [ Compactar e Enviar ]    [ Só compactar (local) ]     │
└─────────────────────────────────────────────────────────────┘
```

### Tela de Envio — Streaming
```
┌─────────────────────────────────────────────────────────────┐
│  Enviando "backup.7z" (4.2 GB)                       ✕      │
│  Link:  app.link/ABCD-1234          [ 📋 Copiar ]           │
│  Status: P2P direto ●  (ou "Relay via VPS ⚠")              │
│  ████████████████░░░░░░░░░░░░░░░░  62%                     │
│  ▸ 3.1 GB / 4.2 GB                                         │
│  ▸ 18.4 MB/s   ▸ ETA 0:48   ▸ 2 peers                      │
└─────────────────────────────────────────────────────────────┘
```

### Tela de Recebimento — Download
```
┌─────────────────────────────────────────────────────────────┐
│  Receber arquivos de "Igor"                                 │
│  📁 pasta-transferida/            [ Ver estrutura ▾ ]        │
│     ├─ backup.7z                   4.2 GB                   │
│     └─ docs/                      2 arquivos                │
│  Destino: C:\Downloads\...        [ Escolher ]              │
│  [ Descompactar ao concluir ☑ ]                             │
│  [ Iniciar Download ]                                       │
└─────────────────────────────────────────────────────────────┘
```

**Diretrizes visuais:**
- Barra de progresso segmentada por chunk (mostra o que já foi confirmado via ACK — base do Resume).
- Indicador persistente de vazão (`MB/s`) e status de conexão no rodapé (diferencial: streaming em tempo real).

---

## 🔒 Segurança & Performance

1. **E2EE:** A VPS nunca lê o conteúdo. A chave de criptografia de cada sessão é gerada localmente no remetente e embutida no fragment/hash da URL enviada ao destinatário.
2. **Uso eficiente da VPS:**
   - Priorizar sempre o WebRTC P2P (custo $0 de tráfego para a VPS).
   - Rate limiting rigoroso no coturn para conexões não pagas (evitar custos abusivos de banda).
3. **Gestão de memória:** nunca carregar arquivos inteiros em RAM; processar estritamente em streams de chunks (`Channel<T>` + backpressure).

---

## 📅 Roadmap de Testes (Checklist do Agente)

- [ ] Testar envio entre duas redes domésticas normais (atrás de roteadores Wi-Fi/NAT).
- [ ] Testar envio com queda repentina de conexão e verificar se o Resume funciona sem corromper o arquivo.
- [ ] Testar descompactação em tempo real (começar a descompactar com o download ainda em 50%).
- [ ] Medir consumo de CPU/RAM em arquivos de 20GB+.
- [ ] Validar estabilidade do SIPSorcery sob alta vazão; se necessário, trocar por binding nativo `libdatachannel`.
