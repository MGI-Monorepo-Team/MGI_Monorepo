using System;

[Serializable]
public class Wallet
{
    public string player_id;
    public int coins;
    public int gems;
    public int coaching_credits;
    public string last_updated;
}

[Serializable]
public class WalletTransaction
{
    public string id;
    public string player_id;
    public int amount;
    public string currency;
    public string type;
    public string timestamp;
    public string source;
    // Optional external event identity used to make imported rewards replay-safe.
    public string event_id;
}
