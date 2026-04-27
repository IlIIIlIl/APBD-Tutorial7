using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Data;
using WebApplication1.DTOs;

[ApiController]
[Route("api/[controller]")]
public class AppointmentsController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public AppointmentsController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    
    
    [HttpGet]
    public async Task<IActionResult> GetAppointments(
        [FromQuery] string? status,
        [FromQuery] string? patientLastName)
    {
        var result = new List<AppointmentListDto>();

        var connectionString = _configuration.GetConnectionString("DefaultConnection");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(@"
            SELECT
                a.IdAppointment,
                a.AppointmentDate,
                a.Status,
                a.Reason,
                p.FirstName + ' ' + p.LastName AS PatientFullName,
                p.Email
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            WHERE (@Status IS NULL OR a.Status = @Status)
              AND (@PatientLastName IS NULL OR p.LastName = @PatientLastName)
            ORDER BY a.AppointmentDate;
        ", connection);

        command.Parameters.Add("@Status", SqlDbType.NVarChar).Value =
            (object?)status ?? DBNull.Value;

        command.Parameters.Add("@PatientLastName", SqlDbType.NVarChar).Value =
            (object?)patientLastName ?? DBNull.Value;

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            result.Add(new AppointmentListDto
            {
                IdAppointment = reader.GetInt32(0),
                AppointmentDate = reader.GetDateTime(1),
                Status = reader.GetString(2),
                Reason = reader.GetString(3),
                PatientFullName = reader.GetString(4),
                PatientEmail = reader.GetString(5)
            });
        }

        return Ok(result);
    }
    
    
    
    
    [HttpGet("{id}")]
    public async Task<IActionResult> GetAppointment(int id)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(@"
        SELECT 
            a.IdAppointment,
            a.AppointmentDate,
            a.Status,
            a.Reason,
            p.FirstName + ' ' + p.LastName,
            p.Email,
            p.PhoneNumber,
            d.FirstName + ' ' + d.LastName,
            d.LicenseNumber,
            a.InternalNotes,
            a.CreatedAt
        FROM Appointments a
        JOIN Patients p ON p.IdPatient = a.IdPatient
        JOIN Doctors d ON d.IdDoctor = a.IdDoctor
        WHERE a.IdAppointment = @Id
    ", connection);

        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
            return NotFound(new ErrorResponseDto { Message = "Appointment not found" });

        var dto = new AppointmentDetailsDto
        {
            IdAppointment = reader.GetInt32(0),
            AppointmentDate = reader.GetDateTime(1),
            Status = reader.GetString(2),
            Reason = reader.GetString(3),
            PatientFullName = reader.GetString(4),
            PatientEmail = reader.GetString(5),
            PatientPhone = reader.GetString(6),
            DoctorFullName = reader.GetString(7),
            DoctorLicenseNumber = reader.GetString(8),
            InternalNotes = reader.IsDBNull(9) ? null : reader.GetString(9),
            CreatedAt = reader.GetDateTime(10)
        };

        return Ok(dto);
    }
    
    
    
    
    [HttpPost]
    public async Task<IActionResult> CreateAppointment(CreateAppointmentRequestDto dto)
    {
        if (dto.AppointmentDate < DateTime.Now)
            return BadRequest(new ErrorResponseDto { Message = "Date cannot be in the past" });

        if (string.IsNullOrWhiteSpace(dto.Reason) || dto.Reason.Length > 250)
            return BadRequest(new ErrorResponseDto { Message = "Invalid reason" });

        var connStr = _configuration.GetConnectionString("DefaultConnection");

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();


        await using (var checkCmd = new SqlCommand(@"
        SELECT COUNT(1)
        FROM Appointments
        WHERE IdDoctor = @Doctor AND AppointmentDate = @Date AND Status = 'Scheduled'
    ", conn))
        {
            checkCmd.Parameters.AddWithValue("@Doctor", dto.IdDoctor);
            checkCmd.Parameters.AddWithValue("@Date", dto.AppointmentDate);

            var exists = (int)await checkCmd.ExecuteScalarAsync();

            if (exists > 0)
                return Conflict(new ErrorResponseDto { Message = "Doctor already has appointment at that time" });
        }

        await using var cmd = new SqlCommand(@"
        INSERT INTO Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason, CreatedAt)
        VALUES (@Patient, @Doctor, @Date, 'Scheduled', @Reason, GETDATE());
        SELECT SCOPE_IDENTITY();
    ", conn);

        cmd.Parameters.AddWithValue("@Patient", dto.IdPatient);
        cmd.Parameters.AddWithValue("@Doctor", dto.IdDoctor);
        cmd.Parameters.AddWithValue("@Date", dto.AppointmentDate);
        cmd.Parameters.AddWithValue("@Reason", dto.Reason);

        var newId = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        return Created($"/api/appointments/{newId}", new { id = newId });
    }
    
    
    
    
    [HttpPut("{id}")]
public async Task<IActionResult> UpdateAppointment(int id, UpdateAppointmentRequestDto dto)
{
    var validStatuses = new[] { "Scheduled", "Completed", "Cancelled" };

    if (!validStatuses.Contains(dto.Status))
        return BadRequest(new ErrorResponseDto { Message = "Invalid status" });

    var connStr = _configuration.GetConnectionString("DefaultConnection");

    await using var conn = new SqlConnection(connStr);
    await conn.OpenAsync();


    string currentStatus;

    await using (var checkCmd = new SqlCommand("SELECT Status FROM Appointments WHERE IdAppointment = @Id", conn))
    {
        checkCmd.Parameters.AddWithValue("@Id", id);

        var result = await checkCmd.ExecuteScalarAsync();

        if (result == null)
            return NotFound(new ErrorResponseDto { Message = "Not found" });

        currentStatus = result.ToString()!;
    }

    if (currentStatus == "Completed" && dto.AppointmentDate != default)
        return Conflict(new ErrorResponseDto { Message = "Cannot change completed appointment date" });


    await using (var conflictCmd = new SqlCommand(@"
        SELECT COUNT(1)
        FROM Appointments
        WHERE IdDoctor = @Doctor AND AppointmentDate = @Date AND IdAppointment <> @Id
    ", conn))
    {
        conflictCmd.Parameters.AddWithValue("@Doctor", dto.IdDoctor);
        conflictCmd.Parameters.AddWithValue("@Date", dto.AppointmentDate);
        conflictCmd.Parameters.AddWithValue("@Id", id);

        if ((int)await conflictCmd.ExecuteScalarAsync() > 0)
            return Conflict(new ErrorResponseDto { Message = "Conflict with another appointment" });
    }

    await using var cmd = new SqlCommand(@"
        UPDATE Appointments
        SET IdPatient=@Patient,
            IdDoctor=@Doctor,
            AppointmentDate=@Date,
            Status=@Status,
            Reason=@Reason,
            InternalNotes=@Notes
        WHERE IdAppointment=@Id
    ", conn);

    cmd.Parameters.AddWithValue("@Patient", dto.IdPatient);
    cmd.Parameters.AddWithValue("@Doctor", dto.IdDoctor);
    cmd.Parameters.AddWithValue("@Date", dto.AppointmentDate);
    cmd.Parameters.AddWithValue("@Status", dto.Status);
    cmd.Parameters.AddWithValue("@Reason", dto.Reason);
    cmd.Parameters.AddWithValue("@Notes", (object?)dto.InternalNotes ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@Id", id);

    await cmd.ExecuteNonQueryAsync();

    return Ok();
}




    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteAppointment(int id)
    {
        var connStr = _configuration.GetConnectionString("DefaultConnection");

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        string? status;

        await using (var cmd = new SqlCommand("SELECT Status FROM Appointments WHERE IdAppointment=@Id", conn))
        {
            cmd.Parameters.AddWithValue("@Id", id);
            status = (string?)await cmd.ExecuteScalarAsync();
        }

        if (status == null)
            return NotFound(new ErrorResponseDto { Message = "Not found" });

        if (status == "Completed")
            return Conflict(new ErrorResponseDto { Message = "Cannot delete completed appointment" });

        await using var deleteCmd = new SqlCommand("DELETE FROM Appointments WHERE IdAppointment=@Id", conn);
        deleteCmd.Parameters.AddWithValue("@Id", id);

        await deleteCmd.ExecuteNonQueryAsync();

        return NoContent();
    }
    
    
}