namespace KeyClick.Core;

public static class TypingChallengeCatalog
{
  public static IReadOnlyList<TypingChallengeDefinition> Passages { get; } =
  [
    new("en-easy-steady-rain", "Steady rain", "A steady rain tapped the window while the quiet room filled with the soft rhythm of careful work.", "en", TypingChallengeDifficulty.Easy, TypingChallengeSource.BuiltIn),
    new("en-easy-morning-path", "Morning path", "Morning light crossed the garden path, and every small leaf seemed brighter after the cool night.", "en", TypingChallengeDifficulty.Easy, TypingChallengeSource.BuiltIn),
    new("en-medium-patient-craft", "Patient craft", "Good work rarely arrives in one dramatic moment. It grows through patient choices, clear feedback, and the courage to revise what almost works.", "en", TypingChallengeDifficulty.Medium, TypingChallengeSource.BuiltIn),
    new("en-medium-city-library", "The city library", "At the center of the city, an old library kept a calm corner for curious people who wanted to follow an idea without interruption.", "en", TypingChallengeDifficulty.Medium, TypingChallengeSource.BuiltIn),
    new("en-hard-precise-systems", "Precise systems", "Reliable systems emerge when precise boundaries, observable behavior, and modest assumptions are combined with tests that challenge every convenient shortcut.", "en", TypingChallengeDifficulty.Hard, TypingChallengeSource.BuiltIn),
    new("en-hard-distant-horizon", "Distant horizon", "Beyond the familiar horizon, intricate patterns of wind and water continually reshape the coast, proving that persistence can transform even the hardest surface.", "en", TypingChallengeDifficulty.Hard, TypingChallengeSource.BuiltIn),
    new("fr-easy-pluie-calme", "Pluie calme", "Une pluie calme frappe la fenêtre pendant que la pièce tranquille suit le rythme doux d'un travail attentif.", "fr", TypingChallengeDifficulty.Easy, TypingChallengeSource.BuiltIn),
    new("fr-easy-chemin-matin", "Chemin du matin", "La lumière du matin traverse le jardin, et chaque petite feuille paraît plus vive après la nuit fraîche.", "fr", TypingChallengeDifficulty.Easy, TypingChallengeSource.BuiltIn),
    new("fr-medium-travail-patient", "Travail patient", "Un bon travail naît rarement en un seul instant. Il grandit grâce à des choix patients, des retours clairs et le courage de corriger.", "fr", TypingChallengeDifficulty.Medium, TypingChallengeSource.BuiltIn),
    new("fr-medium-bibliotheque", "La bibliothèque", "Au centre de la ville, une ancienne bibliothèque offre un coin paisible aux personnes curieuses qui souhaitent suivre une idée sans interruption.", "fr", TypingChallengeDifficulty.Medium, TypingChallengeSource.BuiltIn),
    new("fr-hard-systemes-fiables", "Systèmes fiables", "Les systèmes fiables apparaissent lorsque des limites précises, des comportements observables et des hypothèses modestes rencontrent des tests exigeants.", "fr", TypingChallengeDifficulty.Hard, TypingChallengeSource.BuiltIn),
    new("fr-hard-horizon", "Horizon lointain", "Au-delà de l'horizon familier, les mouvements complexes du vent et de l'eau redessinent sans cesse la côte et transforment patiemment sa surface.", "fr", TypingChallengeDifficulty.Hard, TypingChallengeSource.BuiltIn)
  ];

  public static IReadOnlyList<TypingChallengeDefinition> Filter(string language, TypingChallengeDifficulty difficulty,
    IReadOnlySet<string>? favorites = null) => Passages
      .Where(value => string.Equals(value.Language, language, StringComparison.OrdinalIgnoreCase) && value.Difficulty == difficulty)
      .Select(value => value with { IsFavorite = favorites?.Contains(value.Id) == true })
      .ToArray();
}
