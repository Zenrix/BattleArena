using UnityEngine;
using UnityEngine.UI;
using System.Collections;

public partial class UIChat : MonoBehaviour
{
    public static UIChat singleton;
    public GameObject panel;
    public InputField messageInput;
    public Button sendButton;
    public Transform content;
    public ScrollRect scrollRect;
    public Scrollbar scrollbar;
    public KeyCode[] activationKeys = {KeyCode.Return, KeyCode.KeypadEnter};
    public int keepHistory = 100; // only keep 'n' messages

    bool eatActivation;

    public UIChat()
    {
        // assign singleton only once (to work with DontDestroyOnLoad when
        // using Zones / switching scenes)
        if (singleton == null) singleton = this;
    }

    void Update()
    {
        Player player = Player.localPlayer;
        if (player)
        {
            //panel.SetActive(true);

            // character limit
            PlayerChat chat = player.GetComponent<PlayerChat>();
            messageInput.characterLimit = chat.maxLength;

            // activation (ignored once after deselecting, so it doesn't immediately
            // activate again)
            if (Utils.AnyKeyDown(activationKeys) && !eatActivation)
            {
                ShowChatPanel();
                SelectInput();                
            }
                
            eatActivation = false;

            // end edit listener
            messageInput.onEndEdit.SetListener((value) => {
                // submit key pressed? then submit and set new input text
                if (Utils.AnyKeyDown(activationKeys)) {
                    string newinput = chat.OnSubmit(value);
                    messageInput.text = newinput;
                    messageInput.MoveTextEnd(false);
                    eatActivation = true;
                }

                // unfocus the whole chat in any case. otherwise we would scroll or
                // activate the chat window when doing wsad movement afterwards
                UIUtils.DeselectCarefully();
                StartFade();
            });

            // send button
            sendButton.onClick.SetListener(() => {
                // submit and set new input text
                string newinput = chat.OnSubmit(messageInput.text);
                messageInput.text = newinput;
                messageInput.MoveTextEnd(false);

                // unfocus the whole chat in any case. otherwise we would scroll or
                // activate the chat window when doing wsad movement afterwards
                UIUtils.DeselectCarefully();

                StartFade();
            });
        }
        else panel.SetActive(false);
    }

    /// <summary>
    /// Display the ChatPanel onscreen
    /// </summary>
    void ShowChatPanel()
    {
        //when showing the panel, make sure we aren't currently fading it
        StopCoroutine("Fade");

        /*
         * Activate the panel and set alphas for the panel's UI elements
         */
        panel.SetActive(true);

        for(int i = 0; i < content.childCount; ++i)
        {
            content.GetChild(i).gameObject.GetComponent<CanvasRenderer>().SetAlpha(1f);
        }
        panel.GetComponent<CanvasRenderer>().SetAlpha(1f);
        scrollbar.GetComponent<CanvasRenderer>().SetAlpha(1f);
    }

    void AutoScroll()
    {
        // update first so we don't ignore recently added messages, then scroll
        Canvas.ForceUpdateCanvases();
        scrollRect.verticalNormalizedPosition = 0;
    }

    public void AddMessage(ChatMessage message)
    {
        // delete old messages so the UI doesn't eat too much performance.
        // => every Destroy call causes a lag because of a UI rebuild
        // => it's best to destroy a lot of messages at once so we don't
        //    experience that lag after every new chat message
        if (content.childCount >= keepHistory) {
            for (int i = 0; i < content.childCount / 2; ++i)
                Destroy(content.GetChild(i).gameObject);
        }

        // instantiate and initialize text prefab
        GameObject go = Instantiate(message.textPrefab);
        go.transform.SetParent(content.transform, false);
        go.GetComponent<Text>().text = message.Construct();
        go.GetComponent<UIChatEntry>().message = message;

        AutoScroll();
    }

    // called by chat entries when clicked
    public void OnEntryClicked(UIChatEntry entry)
    {
        // any reply prefix?
        if (!string.IsNullOrWhiteSpace(entry.message.replyPrefix))
        {
            // set text to reply prefix
            messageInput.text = entry.message.replyPrefix;

            SelectInput();

            // move cursor to end (doesn't work in here, needs small delay)
            Invoke(nameof(MoveTextEnd), 0.1f);
        }
    }

    /// <summary>
    /// Select the InputField for messages
    /// </summary>
    void SelectInput()
    {
        //display the InputField
        messageInput.gameObject.SetActive(true);

        // activate message selection
        messageInput.Select();
    }

    /// <summary>
    /// Start fading of the Chat Panel
    /// </summary>
    void StartFade()
    {
        ShowChatPanel();
        messageInput.gameObject.SetActive(false);
        StartCoroutine("Fade");
    }

    /// <summary>
    /// Fade the Chat Panel over an amount of time
    /// </summary>
    /// <returns>IEnumerator for coroutine functionality, to fade the panel over time</returns>
    IEnumerator Fade()
    {
        //wait an amount of time before fading the chat
        yield return new WaitForSeconds(4f);

        //how much time to take to fade in seconds
        float startFadeTime = 3f;

        //the increment at which to fade, and the alpha increment rate to fade at
        float incrementTime = .01f;
        float incrementAlphaFade = incrementTime / startFadeTime;

        //the current alpha of objects
        float currentAlpha = 1f;

        CanvasRenderer panelBackground = panel.GetComponent<CanvasRenderer>();
        CanvasRenderer scrollbarRenderer = scrollbar.GetComponent<CanvasRenderer>();

        for (float waitTime = startFadeTime; waitTime > 0; waitTime -= incrementTime)
        {
            //Debug.Log("fading from " + cr.GetAlpha().ToString() + " to " + (cr.GetAlpha() - incrementAlphaFade).ToString());
            currentAlpha -= incrementAlphaFade;

            /*
             * Fade each alpha progressively towards 0
             */
            panelBackground.SetAlpha(currentAlpha);
            scrollbarRenderer.SetAlpha(currentAlpha);
            for (int i = 0; i < content.childCount; ++i)
            {
                content.GetChild(i).gameObject.GetComponent<CanvasRenderer>().SetAlpha(currentAlpha);
            }
            yield return null;
        }

        panel.SetActive(false);
    }

    void MoveTextEnd()
    {
        messageInput.MoveTextEnd(false);
    }
}
