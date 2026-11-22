using Microsoft.EntityFrameworkCore;

namespace School.Training;

/// <summary>Full attempt detail shared by staff review and the student records page.</summary>
public static class ExamReview
{
    public static async Task<object> BuildAsync(SchoolDbContext db, TrainingExam exam, ExamAttempt attempt, CancellationToken ct)
    {
        var questions = await db.ExamPaperQuestions.AsNoTracking()
            .Where(item => item.PaperId == exam.PaperId).OrderBy(item => item.SortOrder).ToListAsync(ct);
        var answers = await db.ExamAnswers.AsNoTracking()
            .Where(item => item.AttemptId == attempt.Id).ToListAsync(ct);
        var byQuestion = answers.ToDictionary(item => item.PaperQuestionId, StringComparer.Ordinal);
        var rows = questions.Select(question =>
        {
            var answer = byQuestion.GetValueOrDefault(question.Id);
            var selected = answer == null ? [] : ExamLogic.DeserializeSelection(answer.SelectedJson);
            var automatic = question.PracticalNodeId != "";
            var answered = question.Type == "practical"
                ? automatic ? answer?.AnsweredAt != null : !string.IsNullOrWhiteSpace(answer?.TextAnswer)
                : selected.Length > 0;
            return new
            {
                paperQuestionId = question.Id,
                question.Type,
                question.Stem,
                options = ExamLogic.DeserializeOptions(question.OptionsJson),
                answers = ExamLogic.DeserializeAnswers(question.AnswerJson),
                question.Analysis,
                maxScore = question.Score,
                question.SortOrder,
                selected,
                textAnswer = answer?.TextAnswer ?? "",
                scoreAwarded = answer?.ScoreAwarded,
                automaticPractical = automatic,
                practicalDetails = automatic && answer != null
                    ? ClassroomCaseScoring.ScoreDetails(answer.PracticalDetailsJson) : [],
                comment = answer?.Comment ?? "",
                correct = ExamLogic.IsObjective(question.Type) && ExamLogic.IsCorrect(question.AnswerJson, selected),
                answered
            };
        }).ToList();
        return new
        {
            attemptId = attempt.Id,
            examId = exam.Id,
            attempt.StudentName,
            attempt.StudentUserName,
            attempt.StudentNumber,
            attempt.ClassName,
            attempt.Status,
            attempt.StartedAt,
            attempt.Deadline,
            attempt.SubmittedAt,
            attempt.AutoSubmitted,
            attempt.ObjectiveScore,
            attempt.PracticalScore,
            attempt.TotalScore,
            attempt.MaxScore,
            attempt.GradedAt,
            pendingPractical = rows.Count(row => row.Type == "practical" && row.scoreAwarded == null),
            questions = rows
        };
    }
}
