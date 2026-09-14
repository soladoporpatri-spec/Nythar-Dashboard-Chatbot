# Nythar - Dashboard & Chatbot 🤖📊

<div align="center">
  <em>Um sistema completo de agendamento e gerenciamento SaaS com integração oficial WhatsApp e painel administrativo em tempo real.</em>
</div>

---

### 🚀 Sobre o Projeto
O **Nythar** é uma solução de software SaaS B2B criada para modernizar negócios locais. Ele centraliza as operações diárias, unindo em uma única plataforma a agenda, gestão de clientes (CRM), serviços prestados, relatórios financeiros e automação de atendimento inteligente via **chatbot de WhatsApp**.

O fluxo é integrado: o cliente agenda pelo WhatsApp de forma autônoma 24/7 e a equipe administrativa acompanha, aprova ou ajusta as reservas em tempo real através da Dashboard local.

🔗 **[Acessar a Landing Page](https://soladoporpatri-spec.github.io/Nythar-Dashboard-Chatbot/)** | 🔗 **[Ver a Dashboard em modo Demonstração](https://soladoporpatri-spec.github.io/Nythar-Dashboard-Chatbot/demo/)**

---

### 🛠️ Tecnologias e Arquitetura

O ecossistema é dividido em microsserviços e comunicação em tempo real:

**Backend & APIs (.NET)**
- **C# e .NET 8** para regras de negócio sólidas.
- **ASP.NET Core & SignalR** para WebSockets em tempo real.
- **Entity Framework Core** com **SQLite** (Local) ou **PostgreSQL** (Produção).
- **JWT / BCrypt** para segurança e isolamento multi-tenant (várias lojas num único servidor SaaS).

**Frontend Dashboard (PWA)**
- **HTML, CSS, Vanilla JS** + **Tailwind CSS**.
- **Chart.js** para painéis analíticos.
- **PWA (Service Workers)** para envio de notificações nativas à equipe.

**Integrações WhatsApp & Microserviços (Node.js)**
- **Express.js** no proxy local.
- **whatsapp-web.js (Puppeteer)** como ponte não-oficial / oficial do WhatsApp web.
- **ClosedXML** (exportação) e Webhooks do **Google Sheets**.

---

### 🌟 Funcionalidades Principais

- **Agenda Inteligente:** Detecção automática de horários livres, pausas, feriados e regras de expediente dos profissionais.
- **Self-Service no WhatsApp:** Marcação, cancelamento e reagendamento por parte do cliente diretamente pelo chatbot.
- **CRM e Fidelidade:** Histórico do consumidor e sistema de pontos automatizado.
- **Superadmin:** Painel isolado para criação e faturamento de novas empresas locatárias do software.
- **Alta Resiliência:** Logs com *Serilog* e políticas de retentativas automáticas (Retry Policies) usando *Polly* para garantir que integrações do WhatsApp funcionem 99.9% do tempo.

---

### ⚙️ Como Executar em Ambiente de Desenvolvimento

**Requisitos:**
- Node.js 20 LTS ou superior.
- .NET 8 SDK.
- Google Chrome/Chromium.

**1. Instalação do Projeto e Dependências**
```powershell
npm install
cd WhatsAppBridge
npm install
cd ..
dotnet restore WhatsAppBotSolution.sln
node scripts/gerar-segredos.js
```

**2. Iniciar o Ecossistema**
```powershell
npm run supervise
```
Acesse a Dashboard de desenvolvimento em: `http://127.0.0.1:4000/dashboard-improved.html`.

**3. Bateria de Testes**
```powershell
npm test
dotnet test WhatsAppBot.Tests/WhatsAppBot.Tests.csproj -c Release
```
