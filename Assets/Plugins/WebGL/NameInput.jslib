// PC 브라우저에서 한글 이름을 입력할 수 있도록 브라우저 자체 입력창(prompt)을 띄운다.
// Unity WebGL 입력 칸은 한글 입력기(IME)를 지원하지 않기 때문.
mergeInto(LibraryManager.library, {
  OpenNamePrompt: function (currentPtr, maxLen) {
    var current = UTF8ToString(currentPtr);
    var value = window.prompt("이름을 입력하세요 (최대 " + maxLen + "자)", current);
    if (value === null) return; // 취소하면 그대로 둔다
    SendMessage("DeliveryGame", "OnNameEntered", value);
  }
});
