using ClinicHair.WebApi.Domain.Entities;
using ClinicHair.WebApi.Infrastructure.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ClinicHair.WebApi.Infrastructure.Data;

/// <summary>
/// Popula automaticamente a loja Clinic Hair, o administrador Itamar (senha 311208),
/// os funcionários com horários próprios, os serviços (Corte R$ 30, Barba R$ 30, Corte+Barba R$ 50, Sobrancelha R$ 15),
/// agendamentos de exemplo e arquivos .txt de avaliação.
/// </summary>
public static class ClinicHairSeeder
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicHairDbContext>();
        var txtStorage = scope.ServiceProvider.GetRequiredService<IEvaluationTxtStorageService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("ClinicHairSeeder");

        await db.Database.EnsureCreatedAsync(ct);

        // Garante migração não-destrutiva de colunas e tabelas novas sem jamais apagar o banco de dados existente
        await EnsureSchemaColumnsNonDestructiveAsync(db, ct);

        if (await db.Stores.AnyAsync(ct))
        {
            await EnsureSuperAdminAndBillingSeedAsync(db, ct);
            return;
        }

        logger.LogInformation("Inicializando dados da Barbearia Clinic Hair (Admin: Itamar)...");

        var store = new Store
        {
            Name = "Clinic Hair",
            OpeningTime = "08:00",
            ClosingTime = "20:00",
            WorkingDaysCsv = "1,2,3,4,5,6",
            EvaluationRetentionDays = 30,
            WhatsAppStatus = "DISCONNECTED"
        };
        db.Stores.Add(store);
        await db.SaveChangesAsync(ct);

        var hasher = new PasswordHasher<AdminUser>();
        var admin = new AdminUser
        {
            StoreId = store.Id,
            Username = "Itamar",
            Role = "Admin",
            MustChangePassword = true
        };
        admin.PasswordHash = hasher.HashPassword(admin, "311208");
        db.AdminUsers.Add(admin);

        // Funcionário inicial da loja: Itamar (outros funcionários podem ser adicionados na aba Loja > Funcionários)
        var barberItamar = new Barber
        {
            StoreId = store.Id,
            Name = "Itamar",
            Phone = "(62) 98285-4050",
            Specialty = "Master Barber",
            WorkStartTime = "08:00",
            WorkEndTime = "19:00",
            WorkingDaysCsv = "1,2,3,4,5,6",
            MaxConcurrentPerSlot = 3,
            IsActive = true
        };
        db.Barbers.Add(barberItamar);

        // Serviços Oficiais e Precificação Solicitada (com cores saturadas para gradiente até branco)
        var srvCorte = new ServiceItem
        {
            StoreId = store.Id,
            Name = "Corte",
            Price = 30.00m,
            DurationMinutes = 30,
            ColorHex = "#FF6B00", // Laranja saturado -> Branco
            DisplayOrder = 1,
            IsActive = true
        };
        var srvBarba = new ServiceItem
        {
            StoreId = store.Id,
            Name = "Barba",
            Price = 30.00m,
            DurationMinutes = 30,
            ColorHex = "#2563EB", // Azul Royal saturado -> Branco
            DisplayOrder = 2,
            IsActive = true
        };
        var srvCombo = new ServiceItem
        {
            StoreId = store.Id,
            Name = "Corte + Barba",
            Price = 50.00m,
            DurationMinutes = 60,
            ColorHex = "#7C3AED", // Roxo/Violeta saturado -> Branco
            DisplayOrder = 3,
            IsActive = true
        };
        var srvSobrancelha = new ServiceItem
        {
            StoreId = store.Id,
            Name = "Sobrancelha",
            Price = 15.00m,
            DurationMinutes = 30,
            ColorHex = "#059669", // Verde Esmeralda saturado -> Branco
            DisplayOrder = 4,
            IsActive = true
        };
        db.ServiceItems.AddRange(srvCorte, srvBarba, srvCombo, srvSobrancelha);

        // Clientes do CRM
        var customers = new List<Customer>
        {
            new() { StoreId = store.Id, Name = "Rafael Mendonça", Phone = "5511991234501", TotalAppointments = 8, LastAppointmentAt = DateTimeOffset.UtcNow.AddDays(-1) },
            new() { StoreId = store.Id, Name = "Gustavo Henrique", Phone = "5511991234502", TotalAppointments = 5, LastAppointmentAt = DateTimeOffset.UtcNow.AddHours(-3) },
            new() { StoreId = store.Id, Name = "Bruno Oliveira", Phone = "5511991234503", TotalAppointments = 12, LastAppointmentAt = DateTimeOffset.UtcNow },
            new() { StoreId = store.Id, Name = "Matheus Costa", Phone = "5511991234504", TotalAppointments = 3, LastAppointmentAt = DateTimeOffset.UtcNow.AddDays(-4) },
            new() { StoreId = store.Id, Name = "Diego Martins", Phone = "5511991234505", TotalAppointments = 6, LastAppointmentAt = DateTimeOffset.UtcNow }
        };
        db.Customers.AddRange(customers);
        await db.SaveChangesAsync(ct);

        var todayStr = DateTime.Now.ToString("yyyy-MM-dd");
        var tomorrowStr = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd");
        var blockedDayStr = DateTime.Now.AddDays(3).ToString("yyyy-MM-dd");

        // Dia de não funcionamento (indicado em Amarelo no Calendário)
        db.StoreNonWorkingDays.Add(new StoreNonWorkingDay
        {
            StoreId = store.Id,
            BlockedDate = blockedDayStr,
            Reason = "Manutenção & Treinamento da Equipe"
        });

        // Agendamentos de Hoje e Amanhã (demonstrando também múltiplos agendamentos na mesma hora até o limite de 3)
        var appointments = new List<Appointment>
        {
            new()
            {
                StoreId = store.Id, CustomerId = customers[0].Id, BarberId = barberItamar.Id, ServiceItemId = srvCorte.Id,
                AppointmentDate = todayStr, StartTime = "09:00", PriceCharged = 30.00m, Status = "Completed", Source = "WhatsAppBot"
            },
            new()
            {
                StoreId = store.Id, CustomerId = customers[1].Id, BarberId = barberItamar.Id, ServiceItemId = srvCombo.Id,
                AppointmentDate = todayStr, StartTime = "10:30", PriceCharged = 50.00m, Status = "Completed", Source = "WhatsAppBot"
            },
            new()
            {
                StoreId = store.Id, CustomerId = customers[2].Id, BarberId = barberItamar.Id, ServiceItemId = srvCorte.Id,
                AppointmentDate = todayStr, StartTime = "14:00", PriceCharged = 30.00m, Status = "Confirmed", Source = "WhatsAppBot"
            },
            new()
            {
                StoreId = store.Id, CustomerId = customers[3].Id, BarberId = barberItamar.Id, ServiceItemId = srvBarba.Id,
                AppointmentDate = todayStr, StartTime = "14:00", PriceCharged = 30.00m, Status = "Confirmed", Source = "WhatsAppBot"
            },
            new()
            {
                StoreId = store.Id, CustomerId = customers[4].Id, BarberId = barberItamar.Id, ServiceItemId = srvSobrancelha.Id,
                AppointmentDate = todayStr, StartTime = "14:00", PriceCharged = 15.00m, Status = "Confirmed", Source = "Manual",
                Notes = "3º agendamento das 14:00 com Itamar (Horário 14:00 bloqueado na enquete para Itamar)"
            },
            new()
            {
                StoreId = store.Id, CustomerId = customers[0].Id, BarberId = barberItamar.Id, ServiceItemId = srvCombo.Id,
                AppointmentDate = todayStr, StartTime = "16:30", PriceCharged = 50.00m, Status = "Confirmed", Source = "WhatsAppBot"
            },
            new()
            {
                StoreId = store.Id, CustomerId = customers[2].Id, BarberId = barberItamar.Id, ServiceItemId = srvCorte.Id,
                AppointmentDate = tomorrowStr, StartTime = "11:00", PriceCharged = 30.00m, Status = "Confirmed", Source = "WhatsAppBot"
            },
            new()
            {
                StoreId = store.Id, CustomerId = customers[4].Id, BarberId = barberItamar.Id, ServiceItemId = srvBarba.Id,
                AppointmentDate = tomorrowStr, StartTime = "15:00", PriceCharged = 30.00m, Status = "Confirmed", Source = "WhatsAppBot"
            }
        };

        db.Appointments.AddRange(appointments);
        await db.SaveChangesAsync(ct);

        // Cria avaliações reais em arquivos .txt no disco vinculadas aos atendimentos concluídos
        var evalSamples = new[]
        {
            (Appt: appointments[0], Customer: customers[0], Barber: barberItamar, Service: srvCorte,
                Messages: new[]
                {
                    "Atendimento impecável do Itamar! O corte degradê ficou perfeito.",
                    "Gostei muito do lembrete automático 1 hora antes pelo WhatsApp, super prático."
                }),
            (Appt: appointments[1], Customer: customers[1], Barber: barberItamar, Service: srvCombo,
                Messages: new[]
                {
                    "Fiz Corte + Barba por R$ 50 e valeu cada centavo. Toalha quente excelente!",
                    "Ambiente climatizado e pontualidade nota 10."
                })
        };

        foreach (var sample in evalSamples)
        {
            string? relPath = null;
            string fileName = string.Empty;
            long byteSize = 0;
            var consolidatedLines = new List<string>();

            foreach (var msg in sample.Messages)
            {
                var res = await txtStorage.AppendOrCreateEvaluationTxtAsync(
                    sample.Appt.Id,
                    sample.Appt.AppointmentDate,
                    sample.Service.Name,
                    sample.Barber.Name,
                    msg,
                    relPath,
                    ct);
                fileName = res.FileName;
                relPath = res.RelativePath;
                byteSize = res.ByteSize;
                consolidatedLines.Add(res.SanitizedText);
            }

            db.Evaluations.Add(new Evaluation
            {
                StoreId = store.Id,
                AppointmentId = sample.Appt.Id,
                CustomerId = sample.Customer.Id,
                BarberId = sample.Barber.Id,
                ServiceItemId = sample.Service.Id,
                TxtFileName = fileName,
                TxtRelativePath = relPath ?? string.Empty,
                ConsolidatedText = string.Join(Environment.NewLine, consolidatedLines),
                MessageCount = sample.Messages.Length,
                ByteSize = byteSize,
                IsTxtCompacted = false,
                Sentiment = "Positive",
                WindowExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-35)
            });
        }

        await db.SaveChangesAsync(ct);
        await EnsureSuperAdminAndBillingSeedAsync(db, ct);
        logger.LogInformation("Seed da Clinic Hair e SuperAdmin (Patricio) concluído com sucesso!");
    }

    private static async Task EnsureSuperAdminAndBillingSeedAsync(ClinicHairDbContext db, CancellationToken ct)
    {
        var firstStore = await db.Stores.AsTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        if (firstStore is null) return;

        bool storeChanged = false;
        if (string.IsNullOrWhiteSpace(firstStore.SubscriptionPlan))
        {
            firstStore.SubscriptionPlan = "Plano Pro WhatsApp";
            storeChanged = true;
        }
        if (firstStore.MonthlyFee <= 0)
        {
            firstStore.MonthlyFee = 149.90m;
            storeChanged = true;
        }
        if (string.IsNullOrWhiteSpace(firstStore.SubscriptionStatus))
        {
            firstStore.SubscriptionStatus = "Active";
            storeChanged = true;
        }
        if (string.IsNullOrWhiteSpace(firstStore.NextDueDate))
        {
            firstStore.NextDueDate = DateTime.Now.AddDays(25).ToString("yyyy-MM-dd");
            storeChanged = true;
        }
        if (storeChanged)
        {
            db.Stores.Update(firstStore);
            await db.SaveChangesAsync(ct);
        }

        // Garante que a conta de SuperAdministrador exista sempre (senha inicial obriga troca no primeiro login)
        var hasher = new PasswordHasher<AdminUser>();
        var patricio = await db.AdminUsers
            .AsTracking()
            .FirstOrDefaultAsync(u => u.Username.ToLower() == "patricio", ct);

        if (patricio is null)
        {
            patricio = new AdminUser
            {
                StoreId = firstStore.Id,
                Username = "Patricio",
                Role = "SuperAdmin",
                MustChangePassword = true
            };
            patricio.PasswordHash = hasher.HashPassword(patricio, "Patricio3112");
            db.AdminUsers.Add(patricio);
            await db.SaveChangesAsync(ct);
        }
        else if (patricio.Role != "SuperAdmin")
        {
            // Corrige apenas o perfil; a senha nunca é redefinida automaticamente
            patricio.Role = "SuperAdmin";
            db.AdminUsers.Update(patricio);
            await db.SaveChangesAsync(ct);
        }

        await FlagUsersStillOnDefaultPasswordAsync(db, hasher, ct);

        if (!await db.StoreBillingRecords.AnyAsync(ct))
        {
            db.StoreBillingRecords.Add(new StoreBillingRecord
            {
                StoreId = firstStore.Id,
                StoreName = firstStore.Name,
                Amount = firstStore.MonthlyFee > 0 ? firstStore.MonthlyFee : 149.90m,
                DueDate = DateTime.Now.ToString("yyyy-MM-dd"),
                Status = "Paid",
                Description = "Assinatura Mensal - Plano Pro WhatsApp (Ativação)",
                PaymentLinkOrPix = "00020126360014BR.GOV.BCB.PIX0114CLINICHAIRSAAS",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-5),
                PaidAt = DateTimeOffset.UtcNow.AddDays(-5)
            });
            await db.SaveChangesAsync(ct);
        }

        if (!await db.SystemLogEntries.AnyAsync(ct))
        {
            db.SystemLogEntries.AddRange(
                new SystemLogEntry
                {
                    Level = "SECURITY",
                    Category = "Auth",
                    StoreId = firstStore.Id,
                    StoreName = firstStore.Name,
                    Message = "Conta SuperAdministrador (Patricio) e loja Clinic Hair verificadas e ativas.",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-15)
                },
                new SystemLogEntry
                {
                    Level = "WHATSAPP",
                    Category = "BaileysBridge",
                    StoreId = firstStore.Id,
                    StoreName = firstStore.Name,
                    Message = "Supervisor WhatsApp Bridge (porta 3100) monitorando eventos de enquetes e pareamento.",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
                },
                new SystemLogEntry
                {
                    Level = "BILLING",
                    Category = "SaaS",
                    StoreId = firstStore.Id,
                    StoreName = firstStore.Name,
                    Message = $"Assinatura ativa para '{firstStore.Name}' (R$ {firstStore.MonthlyFee:F2}/mês).",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
                });
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Marca para troca obrigatória qualquer conta que ainda use uma das senhas iniciais de fábrica.
    /// </summary>
    private static async Task FlagUsersStillOnDefaultPasswordAsync(
        ClinicHairDbContext db,
        PasswordHasher<AdminUser> hasher,
        CancellationToken ct)
    {
        string[] factoryPasswords = ["311208", "Patricio3112"];
        var users = await db.AdminUsers.AsTracking().Where(u => !u.MustChangePassword).ToListAsync(ct);
        bool changed = false;
        foreach (var user in users)
        {
            foreach (var candidate in factoryPasswords)
            {
                if (hasher.VerifyHashedPassword(user, user.PasswordHash, candidate) != PasswordVerificationResult.Failed)
                {
                    user.MustChangePassword = true;
                    changed = true;
                    break;
                }
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task EnsureSchemaColumnsNonDestructiveAsync(ClinicHairDbContext db, CancellationToken ct)
    {
        var statements = new[]
        {
            "ALTER TABLE Stores ADD COLUMN LastPairingCode TEXT NULL;",
            "ALTER TABLE Stores ADD COLUMN SubscriptionPlan TEXT NOT NULL DEFAULT 'Plano Pro WhatsApp';",
            "ALTER TABLE Stores ADD COLUMN MonthlyFee TEXT NOT NULL DEFAULT '149.90';",
            "ALTER TABLE Stores ADD COLUMN SubscriptionStatus TEXT NOT NULL DEFAULT 'Active';",
            "ALTER TABLE Stores ADD COLUMN NextDueDate TEXT NOT NULL DEFAULT '2026-12-11';",
            "ALTER TABLE Stores ADD COLUMN IsActive INTEGER NOT NULL DEFAULT 1;",
            "ALTER TABLE Stores ADD COLUMN OwnerPhone TEXT NULL;",
            "ALTER TABLE Stores ADD COLUMN BillingNotes TEXT NULL;",
            "ALTER TABLE Stores ADD COLUMN BotPaused INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE Stores ADD COLUMN BotScheduleEnabled INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE Stores ADD COLUMN BotPauseStart TEXT NULL;",
            "ALTER TABLE Stores ADD COLUMN BotPauseEnd TEXT NULL;",
            "ALTER TABLE Stores ADD COLUMN BotPauseOutsideOpeningHours INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE Stores ADD COLUMN WelcomeMessage TEXT NULL;",
            "ALTER TABLE Stores ADD COLUMN HolidayNoticeMessage TEXT NULL;",
            "ALTER TABLE Stores ADD COLUMN HolidayNoticeUntil TEXT NULL;",
            "ALTER TABLE Stores ADD COLUMN Address TEXT NULL;",
            "ALTER TABLE Stores ADD COLUMN MapLink TEXT NULL;",
            "ALTER TABLE Stores ADD COLUMN Reminder24hEnabled INTEGER NOT NULL DEFAULT 1;",
            "ALTER TABLE Stores ADD COLUMN Reminder24hTemplate TEXT NOT NULL DEFAULT 'Olá {Nome}! Passando para lembrar do seu horário amanhã, {Data} às {Hora}, com {Barbeiro} ({Servico}).';",
            "ALTER TABLE Stores ADD COLUMN ReminderResendHours INTEGER NOT NULL DEFAULT 4;",
            "ALTER TABLE Stores ADD COLUMN MinCancelHours INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE Stores ADD COLUMN AntiSpamMaxPerMinute INTEGER NOT NULL DEFAULT 8;",
            "ALTER TABLE Stores ADD COLUMN BirthdayEnabled INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE Stores ADD COLUMN BirthdayTemplate TEXT NOT NULL DEFAULT 'Feliz aniversário, {Nome}! 🎉 A equipe {Loja} deseja um ótimo dia. Venha comemorar com a gente!';",
            "ALTER TABLE Stores ADD COLUMN MonthlyReportEnabled INTEGER NOT NULL DEFAULT 1;",
            "ALTER TABLE Stores ADD COLUMN LastMonthlyReportSent TEXT NULL;",
            "ALTER TABLE Stores ADD COLUMN MonthlyRevenueGoal TEXT NOT NULL DEFAULT '0.0';",
            "ALTER TABLE Stores ADD COLUMN MonthlyAppointmentsGoal INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE AdminUsers ADD COLUMN MustChangePassword INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE AdminUsers ADD COLUMN FailedLoginCount INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE AdminUsers ADD COLUMN LockoutEndUtc TEXT NULL;",
            "ALTER TABLE AdminUsers ADD COLUMN SessionVersion INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE AdminUsers ADD COLUMN PasswordChangedAt TEXT NULL;",
            "ALTER TABLE Customers ADD COLUMN BirthDate TEXT NULL;",
            "ALTER TABLE Customers ADD COLUMN MarketingConsent TEXT NOT NULL DEFAULT 'Unknown';",
            "ALTER TABLE Customers ADD COLUMN ConsentUpdatedAt TEXT NULL;",
            "ALTER TABLE Customers ADD COLUMN InternalNotes TEXT NULL;",
            "ALTER TABLE Customers ADD COLUMN IsAnonymized INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE Customers ADD COLUMN LastBirthdayGreetingYear INTEGER NULL;",
            "ALTER TABLE Appointments ADD COLUMN Reminder24hSentAtUtc TEXT NULL;",
            "ALTER TABLE Appointments ADD COLUMN Reminder24hPollMessageId TEXT NULL;",
            "ALTER TABLE Appointments ADD COLUMN Reminder24hResentAtUtc TEXT NULL;",
            "ALTER TABLE Appointments ADD COLUMN CustomerConfirmedAtUtc TEXT NULL;",
            "ALTER TABLE Appointments ADD COLUMN ArrivedAtUtc TEXT NULL;",
            "ALTER TABLE Appointments ADD COLUMN ServiceStartedAtUtc TEXT NULL;",
            "ALTER TABLE Appointments ADD COLUMN CompletedAtUtc TEXT NULL;",
            "ALTER TABLE Evaluations ADD COLUMN Rating INTEGER NULL;",
            "ALTER TABLE ConversationSessions ADD COLUMN HumanTakeover INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE ConversationSessions ADD COLUMN HumanTakeoverUntilUtc TEXT NULL;",
            "ALTER TABLE ConversationSessions ADD COLUMN AwaitingHuman INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE ConversationSessions ADD COLUMN AwaitingHumanSinceUtc TEXT NULL;",
            "ALTER TABLE ConversationSessions ADD COLUMN LastInboundAtUtc TEXT NULL;",
            "ALTER TABLE ConversationSessions ADD COLUMN LastNoticeDate TEXT NULL;",
            "ALTER TABLE ConversationSessions ADD COLUMN MisunderstoodCount INTEGER NOT NULL DEFAULT 0;",
            @"CREATE TABLE IF NOT EXISTS RevokedTokens (
                Jti TEXT NOT NULL PRIMARY KEY,
                ExpiresAtUtc TEXT NOT NULL
            );",
            @"CREATE TABLE IF NOT EXISTS QuickReplies (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StoreId INTEGER NOT NULL,
                Keywords TEXT NOT NULL,
                Response TEXT NOT NULL,
                IsActive INTEGER NOT NULL DEFAULT 1,
                CreatedAt TEXT NOT NULL
            );",
            @"CREATE TABLE IF NOT EXISTS RecurringBlocks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StoreId INTEGER NOT NULL,
                BarberId INTEGER NOT NULL DEFAULT 0,
                DayOfWeek INTEGER NOT NULL,
                StartTime TEXT NOT NULL,
                EndTime TEXT NOT NULL,
                Reason TEXT NOT NULL,
                IsActive INTEGER NOT NULL DEFAULT 1,
                CreatedAt TEXT NOT NULL
            );",
            @"CREATE TABLE IF NOT EXISTS WaitlistEntries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StoreId INTEGER NOT NULL,
                CustomerName TEXT NOT NULL,
                Phone TEXT NOT NULL,
                Date TEXT NOT NULL,
                BarberId INTEGER NULL,
                ServiceItemId INTEGER NULL,
                Notes TEXT NULL,
                Status TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                NotifiedAt TEXT NULL
            );",
            @"CREATE TABLE IF NOT EXISTS Campaigns (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StoreId INTEGER NOT NULL,
                Name TEXT NOT NULL,
                Message TEXT NOT NULL,
                SegmentJson TEXT NOT NULL,
                Status TEXT NOT NULL,
                TotalRecipients INTEGER NOT NULL DEFAULT 0,
                SentCount INTEGER NOT NULL DEFAULT 0,
                FailedCount INTEGER NOT NULL DEFAULT 0,
                CreatedBy TEXT NULL,
                CreatedAt TEXT NOT NULL,
                FinishedAt TEXT NULL
            );",
            @"CREATE TABLE IF NOT EXISTS CampaignRecipients (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CampaignId INTEGER NOT NULL,
                CustomerId INTEGER NOT NULL,
                Phone TEXT NOT NULL,
                CustomerName TEXT NOT NULL,
                Status TEXT NOT NULL,
                SentAt TEXT NULL
            );",
            @"CREATE TABLE IF NOT EXISTS CashClosings (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StoreId INTEGER NOT NULL,
                Date TEXT NOT NULL,
                ExpectedPix TEXT NOT NULL,
                ExpectedCard TEXT NOT NULL,
                ExpectedCash TEXT NOT NULL,
                CountedPix TEXT NOT NULL,
                CountedCard TEXT NOT NULL,
                CountedCash TEXT NOT NULL,
                Notes TEXT NULL,
                ClosedBy TEXT NULL,
                ClosedAt TEXT NOT NULL
            );",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_CashClosings_StoreId_Date ON CashClosings (StoreId, Date);",
            @"CREATE TABLE IF NOT EXISTS CustomerPlans (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StoreId INTEGER NOT NULL,
                Name TEXT NOT NULL,
                Description TEXT NULL,
                Price TEXT NOT NULL,
                IncludedServiceIdsCsv TEXT NOT NULL,
                UsesPerMonth INTEGER NOT NULL DEFAULT 0,
                ValidityDays INTEGER NOT NULL DEFAULT 30,
                IsActive INTEGER NOT NULL DEFAULT 1,
                CreatedAt TEXT NOT NULL
            );",
            @"CREATE TABLE IF NOT EXISTS CustomerSubscriptions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StoreId INTEGER NOT NULL,
                CustomerId INTEGER NOT NULL,
                PlanId INTEGER NOT NULL,
                Status TEXT NOT NULL,
                StartDate TEXT NOT NULL,
                CurrentPeriodEnd TEXT NOT NULL,
                UsesThisPeriod INTEGER NOT NULL DEFAULT 0,
                Notes TEXT NULL,
                CreatedAt TEXT NOT NULL,
                CancelledAt TEXT NULL
            );",
            "ALTER TABLE Evaluations ADD COLUMN Sentiment TEXT NULL;",
            "ALTER TABLE Appointments ADD COLUMN EvalPollMessageId TEXT NULL;",
            "ALTER TABLE Appointments ADD COLUMN PaymentMethod TEXT NOT NULL DEFAULT 'PIX';",
            "ALTER TABLE Appointments ADD COLUMN ReminderPollMessageId TEXT NULL;",
            "ALTER TABLE Barbers ADD COLUMN LunchStartTime TEXT NULL DEFAULT '12:00';",
            "ALTER TABLE Barbers ADD COLUMN LunchEndTime TEXT NULL DEFAULT '13:00';",
            "ALTER TABLE Barbers ADD COLUMN CommissionPercentage TEXT NOT NULL DEFAULT '50.0';",
            "ALTER TABLE ConversationSessions ADD COLUMN ReschedulingAppointmentId INTEGER NULL;",
            @"CREATE TABLE IF NOT EXISTS StoreBillingRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StoreId INTEGER NOT NULL,
                StoreName TEXT NOT NULL,
                Amount TEXT NOT NULL,
                DueDate TEXT NOT NULL,
                Status TEXT NOT NULL,
                Description TEXT NOT NULL,
                PaymentLinkOrPix TEXT NULL,
                CreatedAt TEXT NOT NULL,
                PaidAt TEXT NULL
            );",
            @"CREATE TABLE IF NOT EXISTS SystemLogEntries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Level TEXT NOT NULL,
                Category TEXT NOT NULL,
                StoreId INTEGER NULL,
                StoreName TEXT NULL,
                Message TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );"
        };

        // Executa diretamente pelo DbCommand: ExecuteSqlRaw interpreta "{...}" como formato e quebraria
        // os DEFAULTs com variáveis de template ({Nome}, {Loja}, {Barbeiro}).
        var connection = db.Database.GetDbConnection();
        bool openedHere = connection.State == System.Data.ConnectionState.Closed;
        if (openedHere) await connection.OpenAsync(ct);
        try
        {
            foreach (var sql in statements)
            {
                try
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = sql;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Coluna ou tabela já existe (esperado em bancos antigos); demais erros ficam registrados no log do EF
                }
            }
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }

        await ApplyVersionedMigrationsAsync(db, ct);
    }

    /// <summary>
    /// Migrações versionadas (item 86): cada versão roda uma única vez e fica registrada em SchemaVersions.
    /// Os comandos continuam idempotentes (ADD COLUMN que já existe é ignorado), então rodar de novo é seguro.
    /// </summary>
    private static readonly (int Version, string Description, string[] Sql)[] VersionedMigrations =
    [
        (1, "Assinaturas avançadas, SuperAdmin 2FA, onboarding, estoque, fidelidade, página pública e white-label",
        [
            "ALTER TABLE Stores ADD COLUMN PublicSlug TEXT NULL;",
            "ALTER TABLE Stores ADD COLUMN PublicBookingEnabled INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE Stores ADD COLUMN BookingHorizonDays INTEGER NOT NULL DEFAULT 14;",
            "ALTER TABLE Stores ADD COLUMN PixKey TEXT NULL;",
            "ALTER TABLE Stores ADD COLUMN PixMerchantName TEXT NULL;",
            "ALTER TABLE Stores ADD COLUMN PixMerchantCity TEXT NULL;",
            "ALTER TABLE Stores ADD COLUMN OnlinePixEnabled INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE Stores ADD COLUMN LoyaltyEnabled INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE Stores ADD COLUMN LoyaltyPointsPerReal TEXT NOT NULL DEFAULT '1.0';",
            "ALTER TABLE Stores ADD COLUMN LoyaltyRewardPoints INTEGER NOT NULL DEFAULT 300;",
            "ALTER TABLE Stores ADD COLUMN LoyaltyRewardDescription TEXT NOT NULL DEFAULT '1 corte grátis';",
            "ALTER TABLE Stores ADD COLUMN Language TEXT NOT NULL DEFAULT 'pt-BR';",
            "ALTER TABLE Stores ADD COLUMN BrandColor TEXT NULL;",
            "ALTER TABLE Stores ADD COLUMN LogoUrl TEXT NULL;",
            "ALTER TABLE Stores ADD COLUMN BrandDisplayName TEXT NULL;",
            "ALTER TABLE Stores ADD COLUMN SubscriptionGraceDays INTEGER NOT NULL DEFAULT 3;",
            "ALTER TABLE Stores ADD COLUMN RenewalReminderDays INTEGER NOT NULL DEFAULT 3;",
            "ALTER TABLE AdminUsers ADD COLUMN TotpSecret TEXT NULL;",
            "ALTER TABLE AdminUsers ADD COLUMN TotpEnabled INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE Barbers ADD COLUMN CalendarIcsUrl TEXT NULL;",
            "ALTER TABLE Customers ADD COLUMN LoyaltyPoints INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE CustomerPlans ADD COLUMN MaxMembers INTEGER NOT NULL DEFAULT 1;",
            "ALTER TABLE CustomerPlans ADD COLUMN PriorityBookingDays INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE CustomerPlans ADD COLUMN IsPublic INTEGER NOT NULL DEFAULT 1;",
            "ALTER TABLE CustomerSubscriptions ADD COLUMN CreditBalance TEXT NOT NULL DEFAULT '0.0';",
            "ALTER TABLE CustomerSubscriptions ADD COLUMN LastRenewalReminderFor TEXT NULL;",
            @"CREATE TABLE IF NOT EXISTS SubscriptionMembers (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SubscriptionId INTEGER NOT NULL,
                CustomerId INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL
            );",
            @"CREATE TABLE IF NOT EXISTS StoreSignupRequests (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StoreName TEXT NOT NULL,
                OwnerName TEXT NOT NULL,
                OwnerPhone TEXT NOT NULL,
                City TEXT NULL,
                DesiredPlan TEXT NOT NULL,
                Status TEXT NOT NULL,
                ReviewNotes TEXT NULL,
                CreatedStoreId INTEGER NULL,
                CreatedAt TEXT NOT NULL,
                ReviewedAt TEXT NULL
            );",
            @"CREATE TABLE IF NOT EXISTS StockItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StoreId INTEGER NOT NULL,
                Name TEXT NOT NULL,
                Sku TEXT NULL,
                Quantity TEXT NOT NULL,
                MinQuantity TEXT NOT NULL,
                UnitCost TEXT NOT NULL,
                SalePrice TEXT NOT NULL,
                IsActive INTEGER NOT NULL DEFAULT 1,
                CreatedAt TEXT NOT NULL
            );",
            @"CREATE TABLE IF NOT EXISTS StockMovements (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StoreId INTEGER NOT NULL,
                StockItemId INTEGER NOT NULL,
                Delta TEXT NOT NULL,
                Reason TEXT NOT NULL,
                CreatedBy TEXT NULL,
                CreatedAt TEXT NOT NULL
            );",
            @"CREATE TABLE IF NOT EXISTS LoyaltyTransactions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StoreId INTEGER NOT NULL,
                CustomerId INTEGER NOT NULL,
                Points INTEGER NOT NULL,
                Reason TEXT NOT NULL,
                AppointmentId INTEGER NULL,
                CreatedAt TEXT NOT NULL
            );",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_Stores_PublicSlug ON Stores (PublicSlug);"
        ]),
    ];

    private static async Task ApplyVersionedMigrationsAsync(ClinicHairDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        bool openedHere = connection.State == System.Data.ConnectionState.Closed;
        if (openedHere) await connection.OpenAsync(ct);
        try
        {
            await ExecAsync(connection, @"CREATE TABLE IF NOT EXISTS SchemaVersions (
                Version INTEGER PRIMARY KEY,
                Description TEXT NOT NULL,
                AppliedAt TEXT NOT NULL
            );", ct);

            var applied = new HashSet<int>();
            await using (var q = connection.CreateCommand())
            {
                q.CommandText = "SELECT Version FROM SchemaVersions;";
                try
                {
                    await using var reader = await q.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct)) applied.Add(Convert.ToInt32(reader.GetValue(0)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { return; }
            }

            foreach (var (version, description, sql) in VersionedMigrations)
            {
                if (applied.Contains(version)) continue;
                foreach (var stmt in sql) await ExecAsync(connection, stmt, ct);
                await using var ins = connection.CreateCommand();
                ins.CommandText = "INSERT INTO SchemaVersions (Version, Description, AppliedAt) VALUES (@v, @d, @a);";
                AddParam(ins, "@v", version);
                AddParam(ins, "@d", description);
                AddParam(ins, "@a", DateTimeOffset.UtcNow.ToString("O"));
                await ins.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    private static async Task ExecAsync(System.Data.Common.DbConnection connection, string sql, CancellationToken ct)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // coluna/tabela já existe
        }
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
