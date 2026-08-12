namespace MessageService.Web.Dtos;

public record PiiMaskingSettingsDto(bool MaskNationalId, bool MaskMobilePhone, bool MaskLandline, bool MaskNhiCard);
