using AgentHost.Api.Contracts;
using FluentValidation;

namespace AgentHost.Api.Validation;

public class CreateRunRequestValidator : AbstractValidator<CreateRunRequest>
{
    public CreateRunRequestValidator()
    {
        RuleFor(x => x.AgentId).NotEmpty();
        RuleFor(x => x.BudgetMaxUsd).GreaterThan(0).When(x => x.BudgetMaxUsd.HasValue);
    }
}

public class ApprovalRequestValidator : AbstractValidator<ApprovalRequest>
{
    public ApprovalRequestValidator()
    {
        RuleFor(x => x.Decision).NotEmpty().Must(d => d is "approve" or "reject")
            .WithMessage("Decision must be 'approve' or 'reject'");
    }
}

public class AnswerQuestionRequestValidator : AbstractValidator<AnswerQuestionRequest>
{
    public AnswerQuestionRequestValidator()
    {
        RuleFor(x => x.QuestionId).NotEmpty();
        RuleFor(x => x.Answer).NotEmpty();
    }
}

public class CreateAgentRequestValidator : AbstractValidator<CreateAgentRequest>
{
    public CreateAgentRequestValidator()
    {
        RuleFor(x => x.ProjectId).NotEmpty();
        RuleFor(x => x.ManifestYaml).NotEmpty();
    }
}

public class PublishAgentVersionRequestValidator : AbstractValidator<PublishAgentVersionRequest>
{
    public PublishAgentVersionRequestValidator()
    {
        RuleFor(x => x.ManifestYaml).NotEmpty();
    }
}

public class CreateProjectRequestValidator : AbstractValidator<CreateProjectRequest>
{
    public CreateProjectRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty();
        RuleFor(x => x.Slug).NotEmpty().Matches("^[a-z0-9-]+$")
            .WithMessage("Slug must be lowercase alphanumeric with dashes");
        RuleFor(x => x.BudgetMonthlyUsd).GreaterThanOrEqualTo(0);
    }
}

public class UpdateProjectRequestValidator : AbstractValidator<UpdateProjectRequest>
{
    public UpdateProjectRequestValidator()
    {
        RuleFor(x => x.BudgetMonthlyUsd).GreaterThanOrEqualTo(0).When(x => x.BudgetMonthlyUsd.HasValue);
    }
}

public class CreateOrganizationRequestValidator : AbstractValidator<CreateOrganizationRequest>
{
    public CreateOrganizationRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty();
        RuleFor(x => x.Slug).NotEmpty().Matches("^[a-z0-9-]+$")
            .WithMessage("Slug must be lowercase alphanumeric with dashes");
        RuleFor(x => x.Plan).NotEmpty().Must(p => p is "free" or "team" or "business" or "enterprise")
            .WithMessage("Plan must be one of: free, team, business, enterprise");
    }
}

public class CreateWebhookRequestValidator : AbstractValidator<CreateWebhookRequest>
{
    public CreateWebhookRequestValidator()
    {
        RuleFor(x => x.ProjectId).NotEmpty();
        RuleFor(x => x.Url).NotEmpty().Must(u => Uri.TryCreate(u, UriKind.Absolute, out _))
            .WithMessage("Url must be a valid absolute URL");
        RuleFor(x => x.Events).NotEmpty();
    }
}

public class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).Password();
    }
}

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.OrgName).NotEmpty();
        RuleFor(x => x.OrgSlug).NotEmpty().Matches("^[a-z0-9-]+$")
            .WithMessage("Slug must be lowercase alphanumeric with dashes");
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).Password();
    }
}

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public class CreateSecretRequestValidator : AbstractValidator<CreateSecretRequest>
{
    public CreateSecretRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty();
        RuleFor(x => x.Value).NotEmpty();
    }
}

public class UpdateSecretRequestValidator : AbstractValidator<UpdateSecretRequest>
{
    public UpdateSecretRequestValidator()
    {
        RuleFor(x => x.Value).NotEmpty();
    }
}

/// <summary>A password change through PUT /api/users/{id} must clear the same bar as signup.</summary>
public class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.Password!).Password().When(x => !string.IsNullOrEmpty(x.Password));
    }
}

public class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty();
    }
}

public class CreateInvitationRequestValidator : AbstractValidator<CreateInvitationRequest>
{
    public CreateInvitationRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
    }
}

/// <summary>
/// The invitee picks their own password, so it must clear exactly the same bar as signup — this is
/// one of the five entry points where a password is set.
/// </summary>
public class AcceptInvitationRequestValidator : AbstractValidator<AcceptInvitationRequest>
{
    public AcceptInvitationRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty();
        RuleFor(x => x.Password).Password();
    }
}

public class PasswordResetRequestRequestValidator : AbstractValidator<PasswordResetRequestRequest>
{
    public PasswordResetRequestRequestValidator()
    {
        // Note: a malformed address still 400s, which is a *format* judgement and reveals nothing
        // about whether any account exists. Existence is never signalled — see the endpoint.
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
    }
}

/// <summary>Resetting sets a password, so it clears the same bar as signup.</summary>
public class PasswordResetConfirmRequestValidator : AbstractValidator<PasswordResetConfirmRequest>
{
    public PasswordResetConfirmRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty();
        RuleFor(x => x.NewPassword).Password();
    }
}

/// <summary>Changing a password sets a password, so it clears the same bar as signup.</summary>
public class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        RuleFor(x => x.NewPassword).Password();
    }
}
