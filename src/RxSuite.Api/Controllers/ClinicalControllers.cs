using Microsoft.AspNetCore.Mvc;
using RxSuite.Api.Common;
using RxSuite.Application.Abstractions;
using RxSuite.Application.Features.Clinical;
using RxSuite.Domain.Clinical;
using RxSuite.Domain.Common;

namespace RxSuite.Api.Controllers;

#region Drug database and alerts panel (RX-01, RX-02, RX-03)

[Route("api/drugs")]
public sealed class DrugsController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    /// <summary>Searches the central drug database by trade name or ingredient.</summary>
    [HttpGet]
    public Task<ActionResult<IReadOnlyList<DrugDto>>> Search([FromQuery] string term, CancellationToken ct) =>
        Send(new SearchDrugsQuery(term), ct);
}

[Route("api/clinical-checks")]
public sealed class ClinicalChecksController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    /// <summary>
    /// The alerts panel: interactions, allergies, duplicate therapy, age and maximum dose for a patient
    /// and a list of items. Saves nothing. Alerts marked RequiresOverride need a reason at the till.
    /// </summary>
    [HttpPost]
    public Task<ActionResult<IReadOnlyList<ClinicalAlert>>> Check([FromBody] CheckMedicinesQuery query, CancellationToken ct) =>
        Send(query, ct);
}

#endregion

#region Patients and prescribers (US-RX-3, RX-08, RX-09)

[Route("api/patients")]
public sealed class PatientsController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    public sealed record AllergyRequest(string AllergenCode, string? Note);

    [HttpPost]
    public Task<ActionResult<int>> Create([FromBody] CreatePatientCommand command, CancellationToken ct) =>
        SendCreated(command, ct);

    /// <summary>Finds a patient by name, mobile or national ID.</summary>
    [HttpGet]
    public Task<ActionResult<IReadOnlyList<PatientSummaryDto>>> Search([FromQuery] string term, CancellationToken ct) =>
        Send(new SearchPatientsQuery(term), ct);

    /// <summary>Medication profile: allergies, history from every branch and prescriptions still to collect.</summary>
    [HttpGet("{patientId:int}")]
    public Task<ActionResult<PatientProfileDto>> GetProfile(int patientId, CancellationToken ct) =>
        Send(new GetPatientProfileQuery(patientId), ct);

    [HttpPost("{patientId:int}/allergies")]
    public Task<IActionResult> AddAllergy(int patientId, [FromBody] AllergyRequest request, CancellationToken ct) =>
        SendNoContent(new AddPatientAllergyCommand(patientId, request.AllergenCode, request.Note), ct);
}

[Route("api/prescribers")]
public sealed class PrescribersController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    [HttpPost]
    public Task<ActionResult<int>> Create([FromBody] CreatePrescriberCommand command, CancellationToken ct) =>
        SendCreated(command, ct);

    /// <summary>Directory search; the most frequent prescribers come first.</summary>
    [HttpGet]
    public Task<ActionResult<IReadOnlyList<PrescriberDto>>> Search([FromQuery] string? term, CancellationToken ct) =>
        Send(new SearchPrescribersQuery(term), ct);
}

#endregion

#region Prescriptions and images (RX-04, RX-05, RX-06, BR-RX-3)

[Route("api/prescriptions")]
public sealed class PrescriptionsController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    /// <summary>Largest upload accepted by the server; the business rule (Clinical:MaxImageBytes) is checked by the use case.</summary>
    private const long MaxUploadBytes = 10 * 1024 * 1024;

    public sealed record CancelRequest(string Reason);

    /// <summary>Records a prescription and returns the alerts panel for it.</summary>
    [HttpPost]
    public Task<ActionResult<PrescriptionCreated>> Create([FromBody] CreatePrescriptionCommand command, CancellationToken ct) =>
        SendCreated(command, ct);

    /// <summary>The prescription with what has been dispensed and what is left on each line.</summary>
    [HttpGet("{prescriptionId:int}")]
    public Task<ActionResult<PrescriptionDto>> GetById(int prescriptionId, CancellationToken ct) =>
        Send(new GetPrescriptionQuery(prescriptionId), ct);

    /// <summary>"Refills due": prescriptions with something left to collect, optionally for one patient.</summary>
    [HttpGet("open")]
    public Task<ActionResult<IReadOnlyList<OpenPrescriptionDto>>> GetOpen([FromQuery] int? patientId, CancellationToken ct) =>
        Send(new GetOpenPrescriptionsQuery(patientId), ct);

    [HttpPost("{prescriptionId:int}/cancel")]
    public Task<IActionResult> Cancel(int prescriptionId, [FromBody] CancelRequest request, CancellationToken ct) =>
        SendNoContent(new CancelPrescriptionCommand(prescriptionId, request.Reason), ct);

    /// <summary>Attaches a JPEG, PNG or PDF of the paper prescription (multipart field "file"). Stored encrypted.</summary>
    [HttpPost("{prescriptionId:int}/images")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<ActionResult<int>> AddImage(int prescriptionId, IFormFile file, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        return await SendCreated(new AddPrescriptionImageCommand(prescriptionId, file.ContentType, buffer.ToArray()), ct);
    }

    /// <summary>Returns a decrypted image. Every view is written to the audit trail (BR-RX-3).</summary>
    [HttpGet("{prescriptionId:int}/images/{imageId:int}")]
    public async Task<IActionResult> GetImage(int prescriptionId, int imageId, CancellationToken ct)
    {
        var result = await Dispatcher.SendAsync(new GetPrescriptionImageQuery(prescriptionId, imageId), ct);
        if (result.IsFailure) return ToProblem(result.Error);

        // Health data: never kept by the browser or a proxy.
        Response.Headers.CacheControl = "no-store";
        return File(result.Value.Content, result.Value.ContentType);
    }
}

/// <summary>RX-07: dispensing labels of a sale, in Arabic and English.</summary>
[Route("api/sales/{saleId:long}/labels")]
public sealed class DispenseLabelsController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    [HttpGet]
    public Task<ActionResult<IReadOnlyList<DispenseLabelDto>>> Get(long saleId, CancellationToken ct) =>
        Send(new GetDispenseLabelsQuery(saleId), ct);
}

#endregion
