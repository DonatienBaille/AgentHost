using AgentHost.Api.Contracts;
using FluentValidation;

namespace AgentHost.Api.Validation;

public class CreateRunRequestValidator : AbstractValidator<CreateRunRequest>
{
    public CreateRunRequestValidator()
    {
        RuleFor(x => x.ProjectId).NotEmpty();
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
        RuleFor(x => x.OrgId).NotEmpty();
        RuleFor(x => x.ProjectId).NotEmpty();
        RuleFor(x => x.ManifestYaml).NotEmpty();
    }
}

public class CreateProjectRequestValidator : AbstractValidator<CreateProjectRequest>
{
    public CreateProjectRequestValidator()
    {
        RuleFor(x => x.OrgId).NotEmpty();
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
        RuleFor(x => x.OrgId).NotEmpty();
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
    }
}

public class CreateSecretRequestValidator : AbstractValidator<CreateSecretRequest>
{
    public CreateSecretRequestValidator()
    {
        RuleFor(x => x.OrgId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty();
        RuleFor(x => x.Value).NotEmpty();
    }
}
