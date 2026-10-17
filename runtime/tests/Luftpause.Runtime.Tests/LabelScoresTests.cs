namespace Luftpause.Runtime.Tests;

public class LabelScoresTests
{
    [Fact]
    public void A_token_that_starts_a_label_scores_it()
    {
        var scores = LabelScores.From([("pos", -0.1), ("neg", -2.5)], ["positive", "negative"]);

        Assert.Equal(["positive", "negative"], scores.Select(s => s.Label));
        Assert.Equal(-0.1, scores[0].Logprob);
        Assert.Equal(Math.Exp(-0.1) / (Math.Exp(-0.1) + Math.Exp(-2.5)), scores[0].Probability, 12);
        Assert.Equal(1.0, scores.Sum(s => s.Probability), 12);
    }

    [Fact]
    public void Case_and_surrounding_whitespace_of_the_token_are_ignored()
    {
        var scores = LabelScores.From([(" YES", -0.2), ("\tNo\n", -1.0)], ["yes", "No"]);

        Assert.Equal([-0.2, -1.0], scores.Select(s => s.Logprob));
    }

    [Fact]
    public void A_label_takes_the_best_of_its_matching_tokens()
    {
        var scores = LabelScores.From([("p", -3.0), ("Pos", -0.5), ("neg", -1.0)], ["positive", "negative"]);

        Assert.Equal(-0.5, scores.Single(s => s.Label == "positive").Logprob);
    }

    [Fact]
    public void Tokens_that_are_blank_or_not_a_prefix_score_nothing()
    {
        var scores = LabelScores.From([(" ", -0.01), ("positively", -0.02), ("neg", -1.0)], ["positive", "negative"]);

        Assert.Null(scores.Single(s => s.Label == "positive").Logprob);
        Assert.Equal(0, scores.Single(s => s.Label == "positive").Probability);
        Assert.Equal(1.0, scores.Single(s => s.Label == "negative").Probability, 12);
    }

    [Fact]
    public void Ties_keep_the_order_the_labels_were_given_in()
    {
        var scores = LabelScores.From([("b", -1.0), ("a", -1.0)], ["c", "b", "a"]);

        Assert.Equal(["b", "a", "c"], scores.Select(s => s.Label));
    }
}
