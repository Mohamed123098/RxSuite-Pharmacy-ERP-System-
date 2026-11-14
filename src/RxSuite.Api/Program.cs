using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.Extensions.Options;
using RxSuite.Api.BackgroundJobs;
using RxSuite.Api.Common;
using RxSuite.Application;
using RxSuite.Application.Abstractions;
using RxSuite.Domain.Rules;
using RxSuite.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

#region Presentation services (controllers, errors, OpenAPI, CORS)

builder.Services
    .AddControllers()
    // Enums travel as text ("Box", "Cash") instead of numbers, which is easier for the React client.
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();

const string WebClientPolicy = "WebClient";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy(WebClientPolicy, policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

#endregion

#region Authentication (bearer access tokens)

// Login returns an access token; the client sends it as "Authorization: Bearer <token>".
// The token carries the user's tenant, branches and permissions as claims (see AuthController).
builder.Services
    .AddAuthentication(BearerTokenDefaults.AuthenticationScheme)
    .AddBearerToken(options => options.BearerTokenExpiration = TimeSpan.FromHours(8));
builder.Services.AddAuthorization();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

#endregion

#region Business rules (Options pattern)

// The numbers the BRD names are read from the "BusinessRules" section of appsettings.json and
// handed to the other layers as a plain object, so they do not depend on IOptions.
builder.Services.Configure<BusinessRules>(builder.Configuration.GetSection(BusinessRules.SectionName));
builder.Services.AddSingleton(provider => provider.GetRequiredService<IOptions<BusinessRules>>().Value);

#endregion

#region Layers (composition root)

// The only place where all layers meet. Each layer registers its own services.
builder.Services.AddApplication();
builder.Services.AddInfrastructure(
    builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' is not configured."));

#endregion

#region Background jobs

if (builder.Configuration.GetValue("Jobs:Enabled", true))
{
    builder.Services.AddHostedService<NightlyJobsService>();     // subscriptions + expired batches
    builder.Services.AddHostedService<EtaSubmissionService>();   // ETA document queue
}

#endregion

var app = builder.Build();

#region HTTP pipeline

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();   // OpenAPI document at /openapi/v1.json

app.UseHttpsRedirection();
app.UseCors(WebClientPolicy);
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

#endregion

app.Run();
